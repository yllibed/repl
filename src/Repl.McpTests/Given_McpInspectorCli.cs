using System.Diagnostics;
using System.Text.Json;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpInspectorCli
{
	private const string EnableInspectorSmokeVariable = "REPL_RUN_MCP_INSPECTOR_TESTS";
	private const string InspectorPackage = "@modelcontextprotocol/inspector@2.2.0";

	[TestMethod]
	[TestCategory("ExternalToolchain")]
	[Description("Opt-in end-to-end smoke guard: MCP Inspector sees command-backed resources as JSON and receives parseable JSON content.")]
	public async Task When_InspectorReadsCommandBackedResource_Then_MimeTypeMatchesJsonPayload()
	{
		if (!IsInspectorSmokeEnabled())
		{
			Assert.Inconclusive(
				$"Set {EnableInspectorSmokeVariable}=1 to run the MCP Inspector external-toolchain smoke test.");
		}

		var npx = ResolveExecutable(OperatingSystem.IsWindows() ? "npx.cmd" : "npx")
			?? throw new InvalidOperationException(
				$"{EnableInspectorSmokeVariable}=1 was set, but npx was not found on PATH.");
		var dotnet = ResolveExecutable(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
			?? throw new InvalidOperationException(
				$"{EnableInspectorSmokeVariable}=1 was set, but dotnet was not found on PATH.");
		var serverDll = ResolveSampleServerDll();

		var resourcesJson = await RunInspectorAsync(
			npx,
			dotnet,
			serverDll,
			["--method", "resources/list"]).ConfigureAwait(false);
		var readJson = await RunInspectorAsync(
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
	[Description("Opt-in end-to-end compatibility guard: the official MCP Inspector can call a long-running tool and receives the synchronous fallback when it does not negotiate MCP Tasks.")]
	public async Task When_InspectorCallsLongRunningTool_Then_ServerReturnsSynchronousFallback()
	{
		if (!IsInspectorSmokeEnabled())
		{
			Assert.Inconclusive(
				$"Set {EnableInspectorSmokeVariable}=1 to run the MCP Inspector external-toolchain smoke test.");
		}

		var npx = ResolveExecutable(OperatingSystem.IsWindows() ? "npx.cmd" : "npx")
			?? throw new InvalidOperationException(
				$"{EnableInspectorSmokeVariable}=1 was set, but npx was not found on PATH.");
		var dotnet = ResolveExecutable(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
			?? throw new InvalidOperationException(
				$"{EnableInspectorSmokeVariable}=1 was set, but dotnet was not found on PATH.");
		var serverDll = ResolveSampleServerDll();

		var responseJson = await RunInspectorAsync(
			npx,
			dotnet,
			serverDll,
			["--method", "tools/call", "--tool-name", "feedback_demo", "--format", "json"])
			.ConfigureAwait(false);

		using var response = JsonDocument.Parse(responseJson);
		var content = response.RootElement
			.GetProperty("result")
			.GetProperty("content")
			.EnumerateArray()
			.Single()
			.GetProperty("text")
			.GetString();
		using var result = JsonDocument.Parse(content ?? string.Empty);

		result.RootElement.GetProperty("kind").GetString().Should().Be("success");
		result.RootElement.GetProperty("message").GetString().Should().Be("Feedback demo completed.");
	}

	private static bool IsInspectorSmokeEnabled()
	{
		var value = Environment.GetEnvironmentVariable(EnableInspectorSmokeVariable);
		return string.Equals(value, "1", StringComparison.Ordinal)
			|| string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
	}

	private static void AssertResourceMimeType(JsonElement root, string uri, string expectedMimeType)
	{
		var resource = root
			.GetProperty("resources")
			.EnumerateArray()
			.Single(item => string.Equals(item.GetProperty("uri").GetString(), uri, StringComparison.Ordinal));
		resource.GetProperty("mimeType").GetString().Should().Be(expectedMimeType);
	}

	private static async Task<string> RunInspectorAsync(
		string npx,
		string dotnet,
		string serverDll,
		IReadOnlyList<string> methodArguments)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		using var process = new Process
		{
			StartInfo =
			{
				FileName = npx,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			},
		};

		ApplyMinimalEnvironment(process.StartInfo);
		process.StartInfo.ArgumentList.Add("-y");
		process.StartInfo.ArgumentList.Add(InspectorPackage);
		process.StartInfo.ArgumentList.Add("--cli");
		process.StartInfo.ArgumentList.Add(dotnet);
		process.StartInfo.ArgumentList.Add(serverDll);
		process.StartInfo.ArgumentList.Add("mcp");
		process.StartInfo.ArgumentList.Add("serve");
		foreach (var argument in methodArguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			throw new InvalidOperationException($"Failed to start MCP Inspector via '{npx}'.", ex);
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("MCP Inspector CLI did not finish within 2 minutes.");
		}

		var stdout = await stdoutTask.ConfigureAwait(false);
		var stderr = await stderrTask.ConfigureAwait(false);
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"MCP Inspector CLI exited with {process.ExitCode}. Stdout: {stdout} Stderr: {stderr}");
		}

		return stdout;
	}

	private static void ApplyMinimalEnvironment(ProcessStartInfo startInfo)
	{
		startInfo.Environment.Clear();
		CopyEnvironmentVariable(startInfo, "PATH");
		CopyEnvironmentVariable(startInfo, "HOME");
		CopyEnvironmentVariable(startInfo, "USERPROFILE");
		CopyEnvironmentVariable(startInfo, "APPDATA");
		CopyEnvironmentVariable(startInfo, "LOCALAPPDATA");
		CopyEnvironmentVariable(startInfo, "COMSPEC");
		CopyEnvironmentVariable(startInfo, "SystemRoot");
		CopyEnvironmentVariable(startInfo, "WINDIR");
		CopyEnvironmentVariable(startInfo, "PATHEXT");
		CopyEnvironmentVariable(startInfo, "TMPDIR");
		CopyEnvironmentVariable(startInfo, "TMP");
		CopyEnvironmentVariable(startInfo, "TEMP");
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
