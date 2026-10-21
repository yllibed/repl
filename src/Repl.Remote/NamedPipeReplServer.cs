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
	private readonly string _pipeName;
	private readonly Func<CancellationToken, ValueTask<IRemoteReplBackend>> _backendFactory;
	private readonly Action<Exception>? _onConnectionError;
	private long _nextId;
	private int _disposed;

	/// <summary>Starts accepting connections. An already-owned endpoint causes construction to fail.</summary>
	public NamedPipeReplServer(string pipeName, Func<CancellationToken, ValueTask<IRemoteReplBackend>> backendFactory,
		Action<Exception>? onConnectionError = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		ArgumentNullException.ThrowIfNull(backendFactory);
		_pipeName = pipeName;
		_backendFactory = backendFactory;
		_onConnectionError = onConnectionError;
		_first = CreatePipe(first: true);
		Completion = AcceptAsync();
	}

	/// <summary>Gets the accept loop; faults indicate that the endpoint is no longer being served.</summary>
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
			while (true)
			{
				await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
				var id = Interlocked.Increment(ref _nextId);
				_pipes[id] = pipe;
				var task = ServeAsync(pipe);
				_connections[id] = task;
				_ = RemoveCompletedAsync(id, task);
				pipe = CreatePipe(first: false);
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		finally
		{
			if (!ReferenceEquals(pipe, _first)) { await pipe.DisposeAsync().ConfigureAwait(false); }
		}
	}

	private async Task ServeAsync(NamedPipeServerStream pipe)
	{
		try
		{
			var backend = await _backendFactory(_lifetime.Token).ConfigureAwait(false);
			await using var backendScope = backend.ConfigureAwait(false);
			await RemoteReplServer.RunAsync(pipe, backend, _lifetime.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception ex)
		{
			System.Diagnostics.Trace.TraceError("Remote CLI connection failed: {0}", ex);
			_onConnectionError?.Invoke(ex);
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

	/// <summary>Stops accepting, cancels this listener's sessions, and waits up to five seconds for them.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
		try
		{
			try { await _lifetime.CancelAsync().ConfigureAwait(false); }
			finally
			{
				try
				{
#pragma warning disable VSTHRD003 // Transport completion has no thread affinity.
					await Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
				}
				finally
				{
					foreach (var pipe in _pipes.Values) { await pipe.DisposeAsync().ConfigureAwait(false); }
				}
			}
			await Task.WhenAll(_connections.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		finally { await _first.DisposeAsync().ConfigureAwait(false); }
	}
}
