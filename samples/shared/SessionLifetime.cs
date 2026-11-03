namespace Repl.Samples;

// Host and transport notifications must not execute command callbacks on their stopping thread.
internal sealed class SessionLifetime : IAsyncDisposable
{
	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _source = new();
	private readonly CancellationTokenRegistration _stopping;
	private readonly CancellationTokenRegistration _disconnected;
	private Task? _cancellation;

	internal SessionLifetime(CancellationToken stopping, CancellationToken disconnected)
	{
		_stopping = stopping.Register(() => { _ = CancelAsync(); });
		_disconnected = disconnected.Register(() => { _ = CancelAsync(); });
	}

	internal CancellationToken Token => _source.Token;
	internal bool IsCancellationRequested => _source.IsCancellationRequested;

	internal Task CancelAsync()
	{
		lock (_gate) { return _cancellation ??= _source.CancelAsync(); }
	}

	public async ValueTask DisposeAsync()
	{
		await _stopping.DisposeAsync().ConfigureAwait(false);
		await _disconnected.DisposeAsync().ConfigureAwait(false);
		try
		{
			Task cancellation;
			lock (_gate) { cancellation = _cancellation ?? Task.CompletedTask; }
#pragma warning disable VSTHRD003 // Observe callbacks started by an independent host or transport notification.
			await cancellation.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		finally { _source.Dispose(); }
	}
}
