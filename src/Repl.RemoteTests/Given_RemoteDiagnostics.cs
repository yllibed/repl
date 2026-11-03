using System.IO.Pipes;
using AwesomeAssertions;
using Repl.Remote;
using StreamJsonRpc;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteDiagnostics
{
	[TestMethod]
	[Description("A backend failure reaches the listener's error callback, so an operator sees the detail the client is denied.")]
	public async Task When_ABackendMethodFails_Then_TheListenerCallbackReceivesTheException()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var failure = new InvalidOperationException("The index database is locked.");
		var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name,
			_ => ValueTask.FromResult<IRemoteReplBackend>(new FailingBackend(execute: failure)),
			error => reported.TrySetResult(error));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);

		var execute = () => client.ExecuteAsync(new() { Arguments = ["status"] }, deadline.Token);

		var refusal = (await execute.Should().ThrowAsync<RemoteInvocationException>()).Which;
		refusal.Message.Should().Be("The remote backend failed.");
		refusal.Message.Should().NotContain("locked");
		(await reported.Task.WaitAsync(deadline.Token)).Should().BeSameAs(failure);
	}

	[TestMethod]
	[Description("A terminal whose session faults reports the fault to the stream server's error callback and exits with 1.")]
	public async Task When_ATerminalFaults_Then_TheServerCallbackReceivesTheException()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var failure = new InvalidOperationException("The terminal renderer crashed.");
		var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		var (input, output) = await ConnectAsync(deadline.Token);
		await using var inputOwner = input;
		await using var outputOwner = output;
		await using var backend = new FailingBackend(terminal: failure);
		var serving = RemoteReplServer.RunAsync(input, backend, error => reported.TrySetResult(error), deadline.Token);
		await using var client = new RemoteReplClient(output);
		try
		{
			await using var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token);

			(await terminal.Completion.WaitAsync(deadline.Token)).Should().Be(1);
			(await reported.Task.WaitAsync(deadline.Token)).Should().BeSameAs(failure);
		}
		finally
		{
			await deadline.CancelAsync();
			try { await serving; } catch (OperationCanceledException) { }
		}
	}

	[TestMethod]
	[Description("An error callback that throws is contained: the client still gets the sanitized error and the connection keeps serving.")]
	public async Task When_TheErrorCallbackThrows_Then_TheConnectionKeepsServing()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name,
			_ => ValueTask.FromResult<IRemoteReplBackend>(new FailingBackend(execute: new InvalidOperationException("boom"))),
			_ => throw new InvalidOperationException("The logger is broken."));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);

		var execute = () => client.ExecuteAsync(new() { Arguments = ["status"] }, deadline.Token);

		(await execute.Should().ThrowAsync<RemoteInvocationException>()).Which.Message.Should().Be("The remote backend failed.");
		(await client.DescribeAsync(deadline.Token)).Name.Should().Be("failing");
	}

	private static async Task<(NamedPipeServerStream Input, NamedPipeClientStream Output)> ConnectAsync(CancellationToken cancellationToken)
	{
		var name = TestPipeName.Create();
		var input = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		var output = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
		var accept = input.WaitForConnectionAsync(cancellationToken);
		await output.ConnectAsync(cancellationToken).ConfigureAwait(false);
		await accept.ConfigureAwait(false);
		return (input, output);
	}

	private sealed class FailingBackend(Exception? execute = null, Exception? terminal = null) : IRemoteReplBackend
	{
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(new RemoteCommandGraph("failing", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken) =>
			execute is null ? ValueTask.FromResult(new RemoteExecutionResult(0, "success")) : ValueTask.FromException<RemoteExecutionResult>(execute);
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
			Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) =>
			ValueTask.FromResult<IRemoteTerminalSession>(new FaultedTerminal(terminal ?? new InvalidOperationException("No terminal failure configured.")));
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class FaultedTerminal(Exception failure) : IRemoteTerminalSession
	{
		public Task<int> Completion { get; } = Task.FromException<int>(failure);
		public ValueTask InputAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask InterruptAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
