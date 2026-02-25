using System.Collections.Concurrent;

namespace Repl.Testing;

/// <summary>
/// In-memory orchestrator for opening and coordinating multiple REPL test sessions.
/// </summary>
public sealed class ReplTestHost : IAsyncDisposable
{
	private readonly Func<ReplApp> _appFactory;
	private readonly ReplScenarioOptions _options;
	private readonly ConcurrentDictionary<string, ReplSessionHandle> _sessions =
		new(StringComparer.Ordinal);
	private bool _disposed;

	private ReplTestHost(Func<ReplApp> appFactory, ReplScenarioOptions options)
	{
		_appFactory = appFactory;
		_options = options;
	}

	public static ReplTestHost Create(Func<ReplApp> appFactory, Action<ReplScenarioOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(appFactory);
		var options = new ReplScenarioOptions();
		configure?.Invoke(options);
		return new ReplTestHost(appFactory, options);
	}

	public async ValueTask<ReplSessionHandle> OpenSessionAsync(
		SessionDescriptor? descriptor = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		descriptor ??= new SessionDescriptor();
		var handle = await ReplSessionHandle.StartAsync(
			this,
			_appFactory,
			descriptor,
			_options,
			cancellationToken).ConfigureAwait(false);
		if (!_sessions.TryAdd(handle.SessionId, handle))
		{
			await handle.DisposeAsync().ConfigureAwait(false);
			throw new InvalidOperationException($"A session with id '{handle.SessionId}' already exists.");
		}

		return handle;
	}

	public ValueTask<IReadOnlyList<SessionSnapshot>> QuerySessionsAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();
		var snapshots = _sessions.Values
			.Select(static session => session.GetSnapshot())
			.OrderBy(static snapshot => snapshot.SessionId, StringComparer.Ordinal)
			.ToArray();
		return ValueTask.FromResult<IReadOnlyList<SessionSnapshot>>(snapshots);
	}

	internal void RemoveSession(string sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId))
		{
			return;
		}

		_sessions.TryRemove(sessionId, out _);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		foreach (var session in _sessions.Values)
		{
			await session.DisposeAsync().ConfigureAwait(false);
		}

		_sessions.Clear();
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
