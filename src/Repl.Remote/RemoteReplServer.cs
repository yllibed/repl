using System.Text.Json;
using StreamJsonRpc;

namespace Repl.Remote;

/// <summary>Serves one connection, borrowing its streams and backend.</summary>
public static class RemoteReplServer
{
	/// <summary>Serves a duplex stream until disconnection or cancellation.</summary>
	public static Task RunAsync(Stream stream, IRemoteReplBackend backend, CancellationToken cancellationToken = default) =>
		RunAsync(stream, stream, backend, cancellationToken);

	/// <summary>Serves separate input/output streams, for example redirected standard I/O.</summary>
	public static async Task RunAsync(Stream input, Stream output, IRemoteReplBackend backend,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(backend);
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var rpc = RemoteProtocol.CreateRpc(input, output);
		var connection = new Connection(rpc, backend, lifetime.Token);
		await using var connectionScope = connection.ConfigureAwait(false);
		RemoteProtocol.RegisterTarget(rpc, connection);
		rpc.StartListening();
		try { await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false); }
		finally
		{
			try { await lifetime.CancelAsync().ConfigureAwait(false); }
			finally
			{
				rpc.Dispose();
				// Completion stops listening; DispatchCompletion also waits for in-flight backend methods.
#pragma warning disable VSTHRD003 // Incoming dispatches cannot require the caller's UI context.
				await rpc.DispatchCompletion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
		}
	}

	private sealed class Connection(JsonRpc rpc, IRemoteReplBackend backend, CancellationToken lifetime) : IAsyncDisposable
	{
		private readonly SemaphoreSlim _gate = new(1, 1);
		private readonly SemaphoreSlim _terminalIo = new(1, 1);
		private volatile bool _terminalClosing;
		private IRemoteTerminalSession? _terminal;
		private string? _terminalId;
		private Task _terminalCompletion = Task.CompletedTask;
		private CancellationTokenSource? _terminalLifetime;

		[JsonRpcMethod("rpc.discover")]
		public static JsonElement Discover() => RemoteProtocol.Discover();

		[JsonRpcMethod("cli.describe")]
		public async Task<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
			return await backend.DescribeAsync(linked.Token).ConfigureAwait(false);
		}

		[JsonRpcMethod("cli.execute", UseSingleObjectParameterDeserialization = true)]
		public async Task<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
		{
			try { request.Validate(); }
			catch (InvalidOperationException ex) { throw new LocalRpcException(ex.Message) { ErrorCode = -32602 }; }
			if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw Busy(); }
			try
			{
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
				return await backend.ExecuteAsync(request, linked.Token).ConfigureAwait(false);
			}
			finally { _gate.Release(); }
		}

		[JsonRpcMethod("terminal.open")]
		public async Task OpenAsync(string sessionId, RemoteTerminalOptions options, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(sessionId)) { throw new LocalRpcException("A sessionId is required.") { ErrorCode = -32602 }; }
			try { options.Validate(); }
			catch (InvalidOperationException ex) { throw new LocalRpcException(ex.Message) { ErrorCode = -32602 }; }
			if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw Busy(); }
			var terminalLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
			_terminalLifetime = terminalLifetime;
			_terminalClosing = false;
			using var openingCancellation = cancellationToken.Register(terminalLifetime.Cancel);
			_terminalId = sessionId;
			try
			{
				_terminal = await backend.OpenTerminalAsync(options,
					async (text, ct) => await rpc.NotifyWithParameterObjectAsync("terminal.output", new { sessionId, text }).WaitAsync(ct).ConfigureAwait(false),
					_terminalLifetime.Token).ConfigureAwait(false);
				_terminalCompletion = FinishTerminalAsync(_terminal, sessionId);
			}
			catch { _terminalLifetime.Dispose(); _terminalLifetime = null; _terminalId = null; _gate.Release(); throw; }
		}

		[JsonRpcMethod("terminal.input")]
		public async Task InputAsync(string sessionId, string text, CancellationToken cancellationToken)
		{
			if (text is null || text.Length > 16384) { throw new LocalRpcException("Input chunks must contain at most 16384 UTF-16 code units.") { ErrorCode = -32602 }; }
			await UseTerminalAsync(sessionId, (terminal, ct) => terminal.InputAsync(text, ct), cancellationToken).ConfigureAwait(false);
		}

		[JsonRpcMethod("terminal.resize")]
		public async Task ResizeAsync(string sessionId, int columns, int rows, CancellationToken cancellationToken)
		{
			if (columns <= 0 || rows <= 0) { throw new LocalRpcException("Dimensions must be positive.") { ErrorCode = -32602 }; }
			await UseTerminalAsync(sessionId, (terminal, ct) => terminal.ResizeAsync(columns, rows, ct), cancellationToken).ConfigureAwait(false);
		}

		[JsonRpcMethod("terminal.interrupt")]
		public async Task InterruptAsync(string sessionId, CancellationToken cancellationToken) =>
			await UseTerminalAsync(sessionId, static (terminal, ct) => terminal.InterruptAsync(ct), cancellationToken).ConfigureAwait(false);

		private async Task UseTerminalAsync(string sessionId, Func<IRemoteTerminalSession, CancellationToken, ValueTask> operation,
			CancellationToken cancellationToken)
		{
			using var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
			await _terminalIo.WaitAsync(waiting.Token).ConfigureAwait(false);
			try
			{
				var terminal = GetTerminal(sessionId);
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(waiting.Token, _terminalLifetime!.Token);
				await operation(terminal, linked.Token).ConfigureAwait(false);
			}
			finally { _terminalIo.Release(); }
		}

		[JsonRpcMethod("terminal.close")]
		public async Task CloseAsync(string sessionId)
		{
			if (!string.Equals(_terminalId, sessionId, StringComparison.Ordinal))
			{
				throw new LocalRpcException("No terminal with this sessionId.") { ErrorCode = -32002 };
			}
			if (_terminalLifetime is { } cts)
			{
				try { await cts.CancelAsync().ConfigureAwait(false); }
				catch (ObjectDisposedException) { }
			}
#pragma warning disable VSTHRD003 // Transport completion has no thread affinity.
			await _terminalCompletion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}

		private IRemoteTerminalSession GetTerminal(string sessionId) =>
			!_terminalClosing && string.Equals(_terminalId, sessionId, StringComparison.Ordinal) && _terminal is { } terminal ? terminal :
			throw new LocalRpcException("No active terminal with this sessionId.") { ErrorCode = -32002 };

		private async Task FinishTerminalAsync(IRemoteTerminalSession terminal, string sessionId)
		{
			var exitCode = 1;
#pragma warning disable VSTHRD003 // Transport completion has no thread affinity.
			try { exitCode = await terminal.Completion.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
			catch (OperationCanceledException) { exitCode = 130; }
#pragma warning disable CA1031 // A backend failure terminates only its terminal, with a portable exit status.
			catch (Exception ex) { System.Diagnostics.Trace.TraceError("Remote terminal failed: {0}", ex); }
#pragma warning restore CA1031
			finally
			{
				_terminalClosing = true;
				try
				{
					if (_terminalLifetime is { } cts) { await cts.CancelAsync().ConfigureAwait(false); }
				}
#pragma warning disable CA1031 // Cancellation callbacks are application code; still drain I/O and dispose the session if one throws.
				catch (Exception ex) { System.Diagnostics.Trace.TraceError("Remote terminal cancellation failed: {0}", ex); exitCode = 1; }
#pragma warning restore CA1031
				await _terminalIo.WaitAsync().ConfigureAwait(false);
				try { await terminal.DisposeAsync().ConfigureAwait(false); }
#pragma warning disable CA1031 // Cleanup failure must not strand the connection in a permanently busy state.
				catch (Exception ex) { System.Diagnostics.Trace.TraceError("Remote terminal cleanup failed: {0}", ex); exitCode = 1; }
#pragma warning restore CA1031
				finally
				{
					_terminal = null;
					_terminalLifetime?.Dispose();
					_terminalLifetime = null;
					_terminalIo.Release();
					_gate.Release();
				}
			}
			try { await rpc.NotifyWithParameterObjectAsync("terminal.exited", new { sessionId, exitCode }).ConfigureAwait(false); }
			catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException) { }
		}

		private static LocalRpcException Busy() => new("This connection already owns an invocation or terminal.") { ErrorCode = -32001 };

		public async ValueTask DisposeAsync()
		{
			try
			{
				if (_terminalLifetime is { } cts)
				{
					try { await cts.CancelAsync().ConfigureAwait(false); }
					catch (ObjectDisposedException) { }
				}
			}
			finally
			{
#pragma warning disable VSTHRD003 // Terminal completion is independent of the caller's UI context.
				await _terminalCompletion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
		}
	}
}
