using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Repl.Remote;

/// <summary>A local, same-user listener. Each connection owns one backend instance.</summary>
public sealed class NamedPipeReplServer : IAsyncDisposable
{
	private readonly CancellationTokenSource _lifetime = new();
	private readonly ConcurrentDictionary<long, Task> _connections = new();
	private readonly ConcurrentDictionary<long, NamedPipeServerStream> _pipes = new();
	private readonly NamedPipeServerStream _first;
	private NamedPipeServerStream? _accepting;
	private readonly string _pipeName;
	private readonly Func<CancellationToken, ValueTask<IRemoteReplBackend>> _backendFactory;
	private readonly Action<Exception>? _onError;
	private long _nextId;
	private int _disposed;

	/// <summary>Starts accepting connections. An already-owned endpoint causes construction to fail.</summary>
	/// <param name="pipeName">The local endpoint name.</param>
	/// <param name="backendFactory">Creates the backend that owns one connection.</param>
	/// <param name="onError">
	/// Receives the detail of every failure the client sees only as a sanitized error or a dropped connection:
	/// a backend method or terminal failure, a connection that failed, a fault that stops the endpoint
	/// accepting, and a cleanup failure after <see cref="DisposeAsync"/> timed out. It runs on the failing path,
	/// possibly for several connections at once, so it should only record the error and be thread-safe; an
	/// exception it throws is traced and otherwise ignored.
	/// </param>
	public NamedPipeReplServer(string pipeName, Func<CancellationToken, ValueTask<IRemoteReplBackend>> backendFactory,
		Action<Exception>? onError = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		ArgumentNullException.ThrowIfNull(backendFactory);
		_pipeName = pipeName;
		_backendFactory = backendFactory;
		_onError = onError;
		_first = CreatePipe(first: true);
		Completion = AcceptAsync();
	}

	/// <summary>
	/// Gets the accept loop; a fault means the endpoint is no longer being served. The fault is also reported
	/// to the error callback, but a host should observe this task to stop or restart in response.
	/// </summary>
	public Task Completion { get; }

	/// <summary>Connects locally with a caller-specified deadline. The returned client owns its pipe.</summary>
	public static async Task<RemoteReplClient> ConnectAsync(string pipeName, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
			return new RemoteReplClient(pipe, pipe, pipe);
		}
		catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
	}

	private NamedPipeServerStream CreatePipe(bool first) => new(_pipeName, PipeDirection.InOut,
		NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
		PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

	private async Task AcceptAsync()
	{
		var pipe = _first;
		try
		{
			while (Volatile.Read(ref _disposed) == 0)
			{
				Volatile.Write(ref _accepting, pipe);
				if (Volatile.Read(ref _disposed) != 0) { break; }
				await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
				if (Volatile.Read(ref _disposed) != 0) { break; }
				var id = Interlocked.Increment(ref _nextId);
				_pipes[id] = pipe;
				var task = ServeAsync(pipe);
				_connections[id] = task;
				_ = RemoveCompletedAsync(id, task);
				pipe = CreatePipe(first: false);
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is ObjectDisposedException or IOException or System.Net.Sockets.SocketException) { }
		catch (Exception ex)
		{
			// Completion carries the fault, but a host that never awaits it would lose the endpoint silently.
			RemoteProtocol.ReportFailure(_onError, "Remote CLI endpoint stopped accepting: {0}", ex);
			throw;
		}
		finally
		{
			Volatile.Write(ref _accepting, value: null);
			if (!ReferenceEquals(pipe, _first)) { await pipe.DisposeAsync().ConfigureAwait(false); }
		}
	}

	private async Task ServeAsync(NamedPipeServerStream pipe)
	{
		try
		{
			var backend = await _backendFactory(_lifetime.Token).ConfigureAwait(false);
			await using var backendScope = backend.ConfigureAwait(false);
			await RemoteReplServer.RunAsync(pipe, backend, _onError, _lifetime.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception ex)
		{
			RemoteProtocol.ReportFailure(_onError, "Remote CLI connection failed: {0}", ex);
		}
		finally
		{
			// Retain one server handle until shutdown so FirstPipeInstance keeps reserving the name.
			if (!ReferenceEquals(pipe, _first)) { await pipe.DisposeAsync().ConfigureAwait(false); }
			else
			{
				try { if (pipe.IsConnected) { pipe.Disconnect(); } }
				catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
			}
		}
	}

	private async Task RemoveCompletedAsync(long id, Task task)
	{
#pragma warning disable VSTHRD003 // Transport completion has no thread affinity.
		try { await task.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
		finally { _connections.TryRemove(id, out _); _pipes.TryRemove(id, out _); }
	}

	/// <summary>Stops accepting and closes owned pipes, waiting up to five seconds for cancellation callbacks and in-flight sessions.</summary>
	/// <remarks>
	/// A fault that already stopped the endpoint accepting is not rethrown: <see cref="Completion"/> carries it and
	/// the error callback received it, so a host reacting to that fault can still shut down cleanly.
	/// </remarks>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
		var shutdown = ShutdownAsync();
		try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
		catch (TimeoutException)
		{
			_ = ObserveShutdownAsync(shutdown);
			throw;
		}
	}

	private async Task ShutdownAsync()
	{
		var cancellation = _lifetime.CancelAsync();
		try
		{
			// Application callbacks can block cancellation; close transport handles before awaiting them.
			var accepting = Interlocked.Exchange(ref _accepting, value: null);
			var pipes = _pipes.Values.Append(_first);
			if (accepting is not null) { pipes = pipes.Append(accepting); }
			await Task.WhenAll(pipes.Distinct().Select(pipe => pipe.DisposeAsync().AsTask())).ConfigureAwait(false);
		}
		finally
		{
			try { await Task.WhenAll(cancellation, DrainConnectionsAsync()).ConfigureAwait(false); }
			finally { _lifetime.Dispose(); }
		}
	}

	private async Task DrainConnectionsAsync()
	{
#pragma warning disable VSTHRD003 // Acceptance must stop before taking the final snapshot of connection tasks.
		// An accept fault was reported when it happened and stays on Completion; it is not a shutdown failure.
		try { await Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
#pragma warning restore VSTHRD003
		finally { await Task.WhenAll(_connections.Values).ConfigureAwait(false); }
	}

	private async Task ObserveShutdownAsync(Task shutdown)
	{
#pragma warning disable VSTHRD003 // Deferred shutdown outlives the caller's deadline and has no thread affinity.
		try { await shutdown.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
#pragma warning disable CA1031 // A timed-out caller cannot observe later cleanup failures; retain diagnostics without an unobserved task.
		catch (Exception ex) { RemoteProtocol.ReportFailure(_onError, "Remote CLI deferred shutdown failed: {0}", ex); }
#pragma warning restore CA1031
	}
}
