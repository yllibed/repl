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

	/// <summary>
	/// Creates a host over an application factory. The factory is invoked once per session, so each
	/// session gets its own <see cref="ReplApp"/> and service provider.
	/// </summary>
	/// <param name="appFactory">Builds the application under test.</param>
	/// <param name="configure">Adjusts the scenario options shared by every session this host opens.</param>
	/// <returns>A host ready to open sessions.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="appFactory"/> is <see langword="null"/>.</exception>
	public static ReplTestHost Create(Func<ReplApp> appFactory, Action<ReplScenarioOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(appFactory);
		var options = new ReplScenarioOptions();
		configure?.Invoke(options);
		return new ReplTestHost(appFactory, options);
	}

	/// <summary>
	/// Opens a session. Sessions are independent and may run concurrently: application state persists
	/// across the commands of one session and is not shared with another.
	/// </summary>
	/// <param name="descriptor">Describes the simulated transport, terminal and prefilled answers. A default descriptor is used when omitted.</param>
	/// <param name="cancellationToken">Cancels opening the session.</param>
	/// <returns>A handle for running commands in the new session.</returns>
	/// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
	/// <exception cref="InvalidOperationException">A session with the same id is already open.</exception>
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

	/// <summary>
	/// Snapshots every session currently open on this host, ordered by session id so an assertion does
	/// not depend on the order they were opened in.
	/// </summary>
	/// <param name="cancellationToken">Cancels the query.</param>
	/// <returns>One snapshot per open session.</returns>
	/// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
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

	/// <summary>
	/// Disposes every session this host still owns. Disposing twice is a no-op.
	/// </summary>
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
