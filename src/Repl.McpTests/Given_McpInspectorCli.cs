using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
[InspectorEnabled]
public sealed class Given_McpInspectorCli
{
	private const string InspectorPackage = "@modelcontextprotocol/inspector@0.22.0";
	private const string HttpInspectorPackage = "@modelcontextprotocol/inspector@2.6.0";

	[TestMethod]
	[TestCategory("ExternalToolchain")]
	[Description("Opt-in end-to-end smoke guard: MCP Inspector sees command-backed resources as JSON and receives parseable JSON content.")]
	public async Task When_InspectorReadsCommandBackedResource_Then_MimeTypeMatchesJsonPayload()
	{
		var npx = ResolveExecutable(OperatingSystem.IsWindows() ? "npx.cmd" : "npx")
			?? throw new InvalidOperationException(
				$"{InspectorEnabledAttribute.EnvironmentVariable}=1 was set, but npx was not found on PATH.");
		var dotnet = ResolveExecutable(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
			?? throw new InvalidOperationException(
				$"{InspectorEnabledAttribute.EnvironmentVariable}=1 was set, but dotnet was not found on PATH.");
		var serverDll = ResolveSampleServerDll();

		var resourcesJson = await RunStdioInspectorAsync(
			npx,
			dotnet,
			serverDll,
			["--method", "resources/list"]).ConfigureAwait(false);
		var readJson = await RunStdioInspectorAsync(
			npx,
			dotnet,
			serverDll,
			["--method", "resources/read", "--uri", "repl://contacts"]).ConfigureAwait(false);

		using var resources = JsonDocument.Parse(resourcesJson);
		using var read = JsonDocument.Parse(readJson);

		AssertResourceMimeType(resources.RootElement, "repl://contacts", "application/json");
		AssertResourceMimeType(resources.RootElement, "repl://contacts/paged", "application/json");

		var content = read.RootElement.GetProperty("contents").EnumerateArray().Single();
		content.GetProperty("uri").GetString().Should().Be("repl://contacts");
		content.GetProperty("mimeType").GetString().Should().Be("application/json");
		using var resourceText = JsonDocument.Parse(content.GetProperty("text").GetString() ?? string.Empty);
		resourceText.RootElement.ValueKind.Should().NotBe(JsonValueKind.Undefined);
	}

	[TestMethod]
	[TestCategory("ExternalToolchain")]
	[Description("An independent MCP client validates generated tool schemas, sees only allowed commands and invokes a projected command over HTTP.")]
	public async Task When_InspectorCallsAStatelessHttpProjection_Then_TheFilteredCatalogIsUsable()
	{
		var npx = ResolveExecutable(OperatingSystem.IsWindows() ? "npx.cmd" : "npx")
			?? throw new InvalidOperationException("MCP Inspector requires npx on PATH.");
		var app = ReplApp.Create();
		app.Map("greet {name}", (string name) => $"Hello, {name}!");
		app.Map("admin", () => "private");
		await using var host = await McpHttpTestHost.StartAsync((services, target) =>
			app.ConfigureMcpServerOptions(target, options =>
			{
				options.CommandFilter = command => string.Equals(command.Path, "greet {name}", StringComparison.Ordinal);
			}, services)).ConfigureAwait(false);
		string[] connection = [host.Endpoint.AbsoluteUri, "--transport", "http", "--format", "json", "--stored-auth-only"];

		var listing = await RunInspectorAsync(npx, HttpInspectorPackage, connection,
			["--method", "tools/list", "--strict"]).ConfigureAwait(false);
		using var tools = JsonDocument.Parse(SuccessfulOutput(listing));
		var tool = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Should().ContainSingle().Which;
		tool.GetProperty("name").GetString().Should().Be("greet");

		var invocation = await RunInspectorAsync(npx, HttpInspectorPackage, connection,
			["--method", "tools/call", "--tool-name", "greet", "--tool-arg", "name=HTTP"]).ConfigureAwait(false);
		using var response = JsonDocument.Parse(SuccessfulOutput(invocation));
		response.RootElement.GetProperty("result").GetProperty("content").EnumerateArray()
			.Should().Contain(item => item.GetProperty("text").GetString()!.Contains("Hello, HTTP!", StringComparison.Ordinal));
	}

	private static void AssertResourceMimeType(JsonElement root, string uri, string expectedMimeType)
	{
		var resource = root
			.GetProperty("resources")
			.EnumerateArray()
			.Single(item => string.Equals(item.GetProperty("uri").GetString(), uri, StringComparison.Ordinal));
		resource.GetProperty("mimeType").GetString().Should().Be(expectedMimeType);
	}

	private static async Task<string> RunStdioInspectorAsync(
		string npx,
		string dotnet,
		string serverDll,
		IReadOnlyList<string> methodArguments)
	{
		var result = await RunInspectorAsync(npx, InspectorPackage, [dotnet, serverDll, "mcp", "serve"], methodArguments).ConfigureAwait(false);
		return SuccessfulOutput(result);
	}

	private static string SuccessfulOutput(InspectorRun result)
	{
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"MCP Inspector CLI exited with {result.ExitCode}. Stdout: {result.Output} Stderr: {result.Errors}");
		}
		return result.Output;
	}

	private static async Task<InspectorRun> RunInspectorAsync(
		string npx,
		string package,
		IReadOnlyList<string> connectionArguments,
		IReadOnlyList<string> methodArguments)
	{
		using var process = new Process
		{
			StartInfo =
			{
				FileName = OperatingSystem.IsWindows() ? "cmd.exe" : npx,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			},
		};

		ApplyMinimalEnvironment(process.StartInfo);
		if (OperatingSystem.IsWindows())
		{
			process.StartInfo.ArgumentList.Add("/d");
			process.StartInfo.ArgumentList.Add("/c");
			process.StartInfo.ArgumentList.Add(Path.GetFileName(npx));
		}
		process.StartInfo.ArgumentList.Add("-y");
		process.StartInfo.ArgumentList.Add(package);
		process.StartInfo.ArgumentList.Add("--cli");
		foreach (var argument in connectionArguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}
		foreach (var argument in methodArguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		return await RunProcessAsync(process, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
	}

	private static async Task<InspectorRun> RunProcessAsync(Process process, TimeSpan executionTimeout)
	{
		using var timeout = new CancellationTokenSource(executionTimeout);
		var stdout = new ProcessOutput();
		var stderr = new ProcessOutput();
		using var outputCancellation = new CancellationTokenSource();

		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			throw new InvalidOperationException($"Failed to start MCP Inspector via '{process.StartInfo.FileName}'.", ex);
		}

		var stdoutTask = stdout.ReadAsync(process.StandardOutput, outputCancellation.Token);
		var stderrTask = stderr.ReadAsync(process.StandardError, outputCancellation.Token);
		var outputTasks = Task.WhenAll(stdoutTask, stderrTask);
		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			await outputTasks.WaitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException) when (process.HasExited)
			{
				// The process can exit between the timeout and Kill.
			}

			outputCancellation.CancelAfter(TimeSpan.FromSeconds(5));
			try
			{
				await Task.WhenAll(process.WaitForExitAsync(outputCancellation.Token), outputTasks)
					.WaitAsync(outputCancellation.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (outputCancellation.IsCancellationRequested)
			{
				// Report the output already received even if inherited pipe handles stay open.
			}

			throw new TimeoutException(
				$"MCP Inspector CLI did not finish within {executionTimeout}. Stdout: {stdout} Stderr: {stderr}");
		}

		return new InspectorRun(process.ExitCode, stdout.ToString(), stderr.ToString());
	}

	private sealed class ProcessOutput
	{
		private readonly StringBuilder _text = new();

		public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
		{
			var buffer = new char[4096];
			int count;
			while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
			{
				lock (_text)
					_text.Append(buffer, 0, count);
			}
		}

		public override string ToString()
		{
			lock (_text)
				return _text.ToString();
		}
	}

	private sealed record InspectorRun(int ExitCode, string Output, string Errors);

	private static void ApplyMinimalEnvironment(ProcessStartInfo startInfo)
	{
		startInfo.Environment.Clear();
		CopyEnvironmentVariable(startInfo, "PATH");
		CopyEnvironmentVariable(startInfo, "HOME");
		CopyEnvironmentVariable(startInfo, "USERPROFILE");
		CopyEnvironmentVariable(startInfo, "TMPDIR");
		CopyEnvironmentVariable(startInfo, "TMP");
		CopyEnvironmentVariable(startInfo, "TEMP");
		CopyEnvironmentVariable(startInfo, "SystemRoot");
		CopyEnvironmentVariable(startInfo, "ComSpec");
		startInfo.Environment["CI"] = "true";
		startInfo.Environment["NO_COLOR"] = "1";
		startInfo.Environment["npm_config_loglevel"] = "silent";
	}

	private static void CopyEnvironmentVariable(ProcessStartInfo startInfo, string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (!string.IsNullOrEmpty(value))
		{
			startInfo.Environment[name] = value;
		}
	}

	private static string ResolveSampleServerDll()
	{
		var root = ResolveRepositoryRoot();
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
		var sampleBin = Path.Combine(root, "samples", "08-mcp-server", "bin", configuration);
		if (!Directory.Exists(sampleBin))
		{
			throw new DirectoryNotFoundException(
				$"The MCP sample server output directory does not exist: {sampleBin}");
		}

		var candidates = Directory
			.EnumerateFiles(sampleBin, "McpServerSample.dll", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.ToArray();
		if (candidates.Length == 0)
		{
			throw new FileNotFoundException("The MCP sample server must be built before the Inspector smoke test runs.");
		}

		return candidates[0];
	}

	private static string? ResolveExecutable(string executable)
	{
		if (Path.IsPathRooted(executable))
		{
			return File.Exists(executable) ? executable : null;
		}

		var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var extensions = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(executable))
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [string.Empty];

		foreach (var directory in paths)
		{
			foreach (var extension in extensions)
			{
				var candidate = Path.Combine(directory, executable + extension);
				if (File.Exists(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
		}

		return null;
	}

	private static string ResolveRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "src", "Repl.slnx")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new InvalidOperationException("Unable to resolve the repository root from the test assembly path.");
	}
}
