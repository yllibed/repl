using System.Text;
using System.Collections.Concurrent;
using AwesomeAssertions;
using Repl.Remote;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteTerminal
{
	[TestMethod]
	[Description("The real Repl line editor handles remote VT editing, Unicode, resizing and command interruption, then exits without stopping the host.")]
	public async Task When_ARealTerminalIsUsed_Then_EditingAndInterruptionStayInItsSession()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var edited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var dimensions = new TaskCompletionSource<(int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create().UseEmbeddedConsoleProfile().Options(options => options.AmbientCommands.ExitCommandEnabled = true);
		app.Map("echo {text}", (string text) => { edited.TrySetResult(text); return text; });
		app.Map("size", (IReplSessionInfo session) => { dimensions.TrySetResult(session.WindowSize); });
		app.Map("wait", async (CancellationToken ct) =>
		{
			started.TrySetResult();
			try { await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false); }
			finally { stopped.TrySetResult(); }
		});
		var name = $"repl-terminal-{Guid.NewGuid():N}";
		var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var serverOwner = server.ConfigureAwait(false);
		var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
		await using var terminalOwner = terminal.ConfigureAwait(false);
		var chunks = new ConcurrentQueue<string>();
		var output = DrainAsync(terminal, resumed, chunks, deadline.Token);

		await terminal.InputAsync("echo ab\u001b[Dété🦆\r", deadline.Token).ConfigureAwait(false);
		(await edited.Task.WaitAsync(deadline.Token).ConfigureAwait(false)).Should().Be("aété🦆b");
		await terminal.ResizeAsync(120, 45, deadline.Token).ConfigureAwait(false);
		await terminal.InputAsync("size\r", deadline.Token).ConfigureAwait(false);
		(await dimensions.Task.WaitAsync(deadline.Token).ConfigureAwait(false)).Should().Be((120, 45));
		await terminal.InputAsync("wait\r", deadline.Token).ConfigureAwait(false);
		await started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
		await terminal.InterruptAsync(deadline.Token).ConfigureAwait(false);
		await stopped.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
		await resumed.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
		await terminal.InputAsync("exit\r", deadline.Token).ConfigureAwait(false);

		await AssertExitAsync(terminal, chunks, deadline.Token).ConfigureAwait(false);
		(await output.ConfigureAwait(false)).Should().Contain("été🦆");
		(await client.ExecuteAsync(new() { Arguments = ["echo", "still-alive"] }, deadline.Token).ConfigureAwait(false)).ExitCode.Should().Be(0);
	}

	[TestMethod]
	[DataRow(false, false, false, DisplayName = "Control character detach")]
	[DataRow(false, true, false, DisplayName = "Modified key detach")]
	[DataRow(true, false, false, DisplayName = "Local output failure")]
	[DataRow(false, false, true, DisplayName = "Caller cancellation")]
	[Description("Console state is restored after a local detach or output failure, even while the remote session is active.")]
	public async Task When_TheLocalConsoleFinishes_Then_ItsStateIsRestored(bool failOutput, bool modifiedKey, bool cancelInput)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		app.Map("hello", () => "hello");
		var name = $"repl-console-{Guid.NewGuid():N}";
		var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var serverOwner = server.ConfigureAwait(false);
		var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token).ConfigureAwait(false);
		await using var clientOwner = client.ConfigureAwait(false);
		var console = new FakeConsole(failOutput, modifiedKey, cancelInput ? deadline.Cancel : null);

		try { await RemoteConsole.RunAsync(client, console, deadline.Token).ConfigureAwait(false); }
		catch (IOException) when (failOutput) { }
		catch (OperationCanceledException) when (cancelInput && deadline.IsCancellationRequested) { }

		console.Restored.Should().BeTrue();
		console.OutputWritten.Should().BeTrue();
	}

	[TestMethod]
	[Description("Writing an emoji one UTF-16 character at a time preserves the Unicode scalar across JSON terminal notifications.")]
	public async Task When_AHandlerWritesIndividualChars_Then_SurrogatePairsSurviveTheWire()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		app.Map("chars", (IReplIoContext io) =>
		{
#pragma warning disable MA0045 // Regression coverage for synchronous char writes, including split surrogate pairs.
			foreach (var value in "🦆") { io.Output.Write(value); }
#pragma warning restore MA0045
		});
		var name = $"repl-unicode-{Guid.NewGuid():N}";
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		await using var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token);

		await terminal.InputAsync("chars\r", deadline.Token);
		var output = new StringBuilder();
		while (!output.ToString().Contains("🦆", StringComparison.Ordinal))
		{
			output.Append(await terminal.Output.ReadAsync(deadline.Token));
		}

		output.ToString().Should().NotContain("\ufffd");
	}

	private static async Task AssertExitAsync(RemoteTerminal terminal, ConcurrentQueue<string> chunks, CancellationToken ct)
	{
		try { (await terminal.Completion.WaitAsync(ct).ConfigureAwait(false)).Should().Be(0); }
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw new AssertFailedException("Terminal did not exit. Output: " + string.Concat(chunks));
		}
	}

	private static async Task<string> DrainAsync(RemoteTerminal terminal, TaskCompletionSource resumed, ConcurrentQueue<string> chunks, CancellationToken ct)
	{
		var output = new StringBuilder();
		await foreach (var text in terminal.Output.ReadAllAsync(ct).ConfigureAwait(false))
		{
			chunks.Enqueue(text);
			output.Append(text);
			if (output.ToString().Contains("Cancelled.", StringComparison.Ordinal)) { resumed.TrySetResult(); }
		}
		return output.ToString();
	}

	private sealed class FakeConsole(bool failOutput, bool modifiedKey, Action? cancel) : IRemoteConsole, IDisposable
	{
		private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool Restored { get; private set; }
		public bool OutputWritten { get; private set; }
		public IDisposable Enter() => this;
		public (int Columns, int Rows) Size => (80, 24);
		public async ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
		{
			await _written.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return new ConsoleKeyInfo(modifiedKey ? 'd' : '\u0004', ConsoleKey.D, shift: false, alt: false, control: true);
		}
		public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
		{
			OutputWritten = true;
			cancel?.Invoke();
			_written.TrySetResult();
			if (failOutput) { throw new IOException("The local terminal failed."); }
			return ValueTask.CompletedTask;
		}
		public void Dispose() => Restored = true;
	}
}
