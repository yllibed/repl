using AwesomeAssertions;
using Repl.Remote;
using Repl.Remote.TestHost;

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
		await using var terminal = await PtySession.StartAsync("dotnet", [HostingSample.AssemblyPath, "--remote-console", name], deadline.Token);
		await terminal.ExpectAsync("> ", deadline.Token);
		await terminal.SendAsync("health\r", deadline.Token);
		await terminal.ExpectAsync("native-ready-marker", deadline.Token);
		await terminal.ExpectAsync("> ", deadline.Token);
		terminal.Resize(120, 45);
		await terminal.SendAsync("size\r", deadline.Token);
		await terminal.ExpectAsync("native-size:120x45", deadline.Token);
		await terminal.ExpectAsync("> ", deadline.Token);
		await terminal.SendAsync("ec\t ab\x1b[DZ\r", deadline.Token);
		await terminal.ExpectAsync("native-echo:aZb", deadline.Token);
		await terminal.ExpectAsync("> ", deadline.Token);
		await terminal.SendAsync("wait\r", deadline.Token);
		await terminal.ExpectAsync("native-wait-started", deadline.Token);
		await terminal.SendAsync("\x03", deadline.Token);
		await terminal.ExpectAsync("Cancelled.", deadline.Token);
		await terminal.ExpectAsync("> ", deadline.Token);
		await terminal.SendAsync("\x04", deadline.Token);
		(await terminal.WaitForExitAsync(deadline.Token)).Should().Be(130);
		var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		(await client.ExecuteAsync(new() { Arguments = ["health"] }, deadline.Token).ConfigureAwait(false)).ExitCode.Should().Be(0);
	}
}
