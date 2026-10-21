using System.IO.Pipes;
using System.Text.Json;
using AwesomeAssertions;
using Repl.Remote;
using StreamJsonRpc;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteProtocol
{
	[TestMethod]
	[Description("A backend with no engine or service container can serve discovery and typed invocations over a real local connection.")]
	public async Task When_AClientConnects_Then_ItDiscoversAndExecutesJsonValues()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var name = NewPipeName();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend()));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);

		var discovery = await client.DiscoverAsync(deadline.Token);
		var graph = await client.DescribeAsync(deadline.Token);
		var result = await client.ExecuteAsync(new() { Command = "echo", Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["value"] = JsonSerializer.SerializeToElement(new { label = "été 🦆", numbers = (int[])[1, 2] }),
		}, }, deadline.Token);

		discovery.GetProperty("openrpc").GetString().Should().Be("1.3.2");
		discovery.GetProperty("x-cli").GetProperty("version").GetString().Should().Be(RemoteProtocol.Version);
		graph.Name.Should().Be("portable");
		result.ExitCode.Should().Be(7);
		result.Result!.Value.GetProperty("label").GetString().Should().Be("été 🦆");
		typeof(RemoteReplClient).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name)
			.Should().NotContain("Repl.Core").And.NotContain("Repl.Defaults");
	}

	[TestMethod]
	[Description("A terminal reserves only its own connection, preserves VT text and dimensions, and leaves a second client usable.")]
	public async Task When_ATerminalIsOpen_Then_OnlyItsConnectionIsBusy()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var name = NewPipeName();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend()));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		await using var other = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		await using var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token);

		await terminal.InputAsync("été\u001b[D", deadline.Token);
		(await terminal.Output.ReadAsync(deadline.Token)).Should().Be("été\u001b[D");
		await terminal.ResizeAsync(100, 40, deadline.Token);
		(await terminal.Output.ReadAsync(deadline.Token)).Should().Be("100x40");
		var busy = () => client.ExecuteAsync(new() { Arguments = ["echo"] }, deadline.Token);
		(await busy.Should().ThrowAsync<RemoteInvocationException>()).Which.ErrorCode.Should().Be(-32001);
		(await other.ExecuteAsync(new() { Arguments = ["echo"] }, deadline.Token)).ExitCode.Should().Be(7);

		await terminal.InterruptAsync(deadline.Token);
		(await terminal.Completion.WaitAsync(deadline.Token)).Should().Be(130);
		(await client.ExecuteAsync(new() { Arguments = ["echo"] }, deadline.Token)).ExitCode.Should().Be(7);
	}

	[TestMethod]
	[Description("Disposing the stream-based client and stopping its server preserve both caller-owned pipe handles.")]
	public async Task When_AConnectionIsDisposed_Then_BorrowedStreamsStayOpen()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var name = NewPipeName();
		await using var input = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		await using var output = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
		var accept = input.WaitForConnectionAsync(deadline.Token);
		await output.ConnectAsync(deadline.Token);
		await accept;
		await using var backend = new Backend();
		var serving = RemoteReplServer.RunAsync(input, backend, deadline.Token);
		var client = new RemoteReplClient(output);
		await client.DiscoverAsync(deadline.Token);

		await client.DisposeAsync();
		output.CanRead.Should().BeTrue();
		await deadline.CancelAsync();
		try { await serving; } catch (OperationCanceledException) { }
		input.CanRead.Should().BeTrue();
	}

	[TestMethod]
	[Description("An active listener reserves its endpoint even after its first client leaves, preventing a second server from splitting traffic.")]
	public async Task When_AnEndpointIsOwned_Then_ASecondListenerFails()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var name = NewPipeName();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend()));
		await using (var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token)) { await client.DiscoverAsync(deadline.Token); }

		var duplicate = () => new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend()));
		duplicate.Should().Throw<Exception>().Where(error => error is IOException || error is UnauthorizedAccessException);
	}

	[TestMethod]
	[Description("An A-to-B-to-C chain preserves the final JSON result without coupling either forwarding backend to the engine.")]
	public async Task When_AnInvocationCrossesTwoServers_Then_TheResultReturnsToTheOriginalClient()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var cName = NewPipeName();
		await using var cServer = new NamedPipeReplServer(cName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend()));
		var cClient = await NamedPipeReplServer.ConnectAsync(cName, deadline.Token).ConfigureAwait(false);
		await using var cClientOwner = cClient.ConfigureAwait(false);
		var bName = NewPipeName();
		var bServer = new NamedPipeReplServer(bName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend(
			async (request, ct) => await cClient.ExecuteAsync(request, ct).ConfigureAwait(false))));
		await using var bServerOwner = bServer.ConfigureAwait(false);
		var aClient = await NamedPipeReplServer.ConnectAsync(bName, deadline.Token).ConfigureAwait(false);
		await using var aClientOwner = aClient.ConfigureAwait(false);

		var result = await aClient.ExecuteAsync(new() { Command = "echo", Values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			["value"] = JsonSerializer.SerializeToElement("A → B → C"),
		}, }, deadline.Token).ConfigureAwait(false);

		result.ExitCode.Should().Be(7);
		result.Result!.Value.GetString().Should().Be("A → B → C");
	}

	[TestMethod]
	[Description("A callback from C into B's already-active connection fails promptly with busy instead of recursing. New connections are a separate delegation-policy concern.")]
	public async Task When_AChainReentersItsActiveConnection_Then_TheCycleIsRejected()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		RemoteReplClient? aClient = null;
		var cName = NewPipeName();
		var rejected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cServer = new NamedPipeReplServer(cName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend(async (request, ct) =>
		{
			try { return await aClient!.ExecuteAsync(request, ct).ConfigureAwait(false); }
			catch (RemoteInvocationException error) { rejected.TrySetResult(error.ErrorCode); return new(1, "reentrancyRejected"); }
		})));
		await using var cServerOwner = cServer.ConfigureAwait(false);
		var cClient = await NamedPipeReplServer.ConnectAsync(cName, deadline.Token).ConfigureAwait(false);
		await using var cClientOwner = cClient.ConfigureAwait(false);
		var bName = NewPipeName();
		var bServer = new NamedPipeReplServer(bName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend(
			async (request, ct) => await cClient.ExecuteAsync(request, ct).ConfigureAwait(false))));
		await using var bServerOwner = bServer.ConfigureAwait(false);
		aClient = await NamedPipeReplServer.ConnectAsync(bName, deadline.Token).ConfigureAwait(false);
		await using var aClientOwner = aClient.ConfigureAwait(false);

		var result = await aClient.ExecuteAsync(new() { Arguments = ["echo"] }, deadline.Token).ConfigureAwait(false);

		(await rejected.Task.WaitAsync(deadline.Token).ConfigureAwait(false)).Should().Be(-32001);
		result.Kind.Should().Be("reentrancyRejected");
	}

	[TestMethod]
	[Description("Cancelling A propagates through B's forwarded request into C's handler, without disconnecting either listener.")]
	public async Task When_AChainedInvocationIsCancelled_Then_TheFinalHandlerObservesCancellation()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cName = NewPipeName();
		var cServer = new NamedPipeReplServer(cName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend(async (_, ct) =>
		{
			started.TrySetResult();
			try { await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false); }
			finally { stopped.TrySetResult(); }
			return new(0, "success");
		})));
		await using var cServerOwner = cServer.ConfigureAwait(false);
		var cClient = await NamedPipeReplServer.ConnectAsync(cName, deadline.Token).ConfigureAwait(false);
		await using var cClientOwner = cClient.ConfigureAwait(false);
		var bName = NewPipeName();
		var bServer = new NamedPipeReplServer(bName, _ => ValueTask.FromResult<IRemoteReplBackend>(new Backend(
			async (request, ct) => await cClient.ExecuteAsync(request, ct).ConfigureAwait(false))));
		await using var bServerOwner = bServer.ConfigureAwait(false);
		var aClient = await NamedPipeReplServer.ConnectAsync(bName, deadline.Token).ConfigureAwait(false);
		await using var aClientOwner = aClient.ConfigureAwait(false);
		var invocation = aClient.ExecuteAsync(new() { Arguments = ["wait"] }, caller.Token);
		await started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);

		await caller.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // The assertion observes this test's already-started request.
		var observe = async () => await invocation.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		await observe.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		await stopped.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
	}

	private static string NewPipeName() => $"repl-tests-{Guid.NewGuid():N}";

	private sealed class Backend(Func<RemoteInvocationRequest, CancellationToken, ValueTask<RemoteExecutionResult>>? execute = null) : IRemoteReplBackend
	{
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(new RemoteCommandGraph("portable", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken) =>
			execute is null ? ValueTask.FromResult(new RemoteExecutionResult(7, "success", request.Values?.GetValueOrDefault("value"))) : execute(request, cancellationToken);
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
			Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) =>
			ValueTask.FromResult<IRemoteTerminalSession>(new Terminal(writeOutput, cancellationToken));
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class Terminal : IRemoteTerminalSession
	{
		private readonly Func<string, CancellationToken, ValueTask> _write;
		private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly CancellationTokenRegistration _registration;
		internal Terminal(Func<string, CancellationToken, ValueTask> write, CancellationToken lifetime)
		{
			_write = write;
			_registration = lifetime.Register(() => _completion.TrySetResult(130));
		}
		public Task<int> Completion => _completion.Task;
		public ValueTask InputAsync(string text, CancellationToken cancellationToken) => _write(text, cancellationToken);
		public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => _write($"{columns}x{rows}", cancellationToken);
		public ValueTask InterruptAsync(CancellationToken cancellationToken) { _completion.TrySetResult(130); return ValueTask.CompletedTask; }
		public ValueTask DisposeAsync() { _registration.Dispose(); return ValueTask.CompletedTask; }
	}
}
