using System.Diagnostics;
using System.Text.Json;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpInspectorCli
{
	private const string InspectorPackage = "@modelcontextprotocol/inspector@0.22.0";

	[TestMethod]
	[Description("End-to-end smoke guard: MCP Inspector sees command-backed resources as JSON and receives parseable JSON content.")]
	public async Task When_InspectorReadsCommandBackedResource_Then_MimeTypeMatchesJsonPayload()
	{
		var serverDll = ResolveSampleServerDll();

		var resourcesJson = await RunInspectorAsync(
			serverDll,
			["--method", "resources/list"]).ConfigureAwait(false);
		var readJson = await RunInspectorAsync(
			serverDll,
			["--method", "resources/read", "--uri", "repl://contacts"]).ConfigureAwait(false);

		using var resources = JsonDocument.Parse(resourcesJson);
		using var read = JsonDocument.Parse(readJson);

		AssertResourceMimeType(resources.RootElement, "repl://contacts", "application/json");
		AssertResourceMimeType(resources.RootElement, "repl://contacts/paged", "application/json");

		var content = read.RootElement.GetProperty("contents").EnumerateArray().Single();
		content.GetProperty("uri").GetString().Should().Be("repl://contacts");
		content.GetProperty("mimeType").GetString().Should().Be("application/json");

		var contacts = JsonDocument.Parse(content.GetProperty("text").GetString() ?? string.Empty);
		contacts.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
		contacts.RootElement.EnumerateArray().First().GetProperty("name").GetString().Should().Be("Alice");
	}

	private static void AssertResourceMimeType(JsonElement root, string uri, string expectedMimeType)
	{
		var resource = root
			.GetProperty("resources")
			.EnumerateArray()
			.Single(item => string.Equals(item.GetProperty("uri").GetString(), uri, StringComparison.Ordinal));
		resource.GetProperty("mimeType").GetString().Should().Be(expectedMimeType);
	}

	private static async Task<string> RunInspectorAsync(string serverDll, IReadOnlyList<string> methodArguments)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		using var process = new Process
		{
			StartInfo =
			{
				FileName = OperatingSystem.IsWindows() ? "npx.cmd" : "npx",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			},
		};

		process.StartInfo.Environment["npm_config_loglevel"] = "silent";
		process.StartInfo.ArgumentList.Add("-y");
		process.StartInfo.ArgumentList.Add(InspectorPackage);
		process.StartInfo.ArgumentList.Add("--cli");
		process.StartInfo.ArgumentList.Add("dotnet");
		process.StartInfo.ArgumentList.Add(serverDll);
		process.StartInfo.ArgumentList.Add("mcp");
		process.StartInfo.ArgumentList.Add("serve");
		foreach (var argument in methodArguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.Start().Should().BeTrue();
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

	private static string ResolveSampleServerDll()
	{
		var root = ResolveRepositoryRoot();
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
		var serverDll = Path.Combine(
			root,
			"samples",
			"08-mcp-server",
			"bin",
			configuration,
			"net10.0",
			"McpServerSample.dll");

		if (!File.Exists(serverDll))
		{
			throw new FileNotFoundException("The MCP sample server must be built before the Inspector smoke test runs.", serverDll);
		}

		return serverDll;
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
