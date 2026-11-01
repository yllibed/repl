using System.Diagnostics;
using AwesomeAssertions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_NativeConsole
{
	[TestMethod]
	[TestCategory("ExternalToolchain")]
	[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
	[Description("The sample's real native client edits, resizes, interrupts and detaches through a Unix PTY while the original backend keeps running.")]
	public async Task When_TheNativeClientAttaches_Then_ItControlsOnlyItsRemoteSession()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		app.Map("health", () => "native-ready-marker");
		app.Map("size", (IReplSessionInfo info) => $"native-size:{info.WindowSize?.Width}x{info.WindowSize?.Height}");
		app.Map("echo {text}", (string text) => "native-echo:" + text);
		app.Map("wait", async (IReplIoContext io, CancellationToken ct) =>
		{
			await io.Output.WriteLineAsync("native-wait-started").ConfigureAwait(false);
			await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false);
		});
		var name = TestPipeName.Create();
		var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var serverOwner = server.ConfigureAwait(false);
		var root = FindRepositoryRoot();
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
		var assembly = Path.Combine(root, "samples", "05-hosting-remote", "bin", configuration, "net10.0", "HostingRemoteSample.dll");
		var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "console_client.py"));
		start.ArgumentList.Add(assembly);
		start.ArgumentList.Add(name);
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
		try
		{
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			process.ExitCode.Should().Be(0, await error.ConfigureAwait(false));
			(await output.ConfigureAwait(false)).Should().Contain("detach passed");
		}
		finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
		var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		(await client.ExecuteAsync(new() { Arguments = ["health"] }, deadline.Token).ConfigureAwait(false)).ExitCode.Should().Be(0);
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "global.json"))) { return directory.FullName; }
		}
		throw new DirectoryNotFoundException("Could not locate the repository root for the sample executable.");
	}
}
