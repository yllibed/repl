using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
	// The host, not the handle, invokes the factory — because that is what lets it see every DISTINCT
	// ReplApp a session was opened against and dispose each one's root ServiceProvider exactly once at
	// teardown. Keyed by reference identity, deliberately: a factory that closes over and returns the
	// SAME app for every session (so several sessions can share one container) must count as one entry,
	// not one per session, or the second session's own scope would be torn down with the first's.
	private readonly ConcurrentDictionary<ReplApp, byte> _apps = new(ReferenceEqualityComparer.Instance);
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
		// Before the factory call, not after: the factory can carry arbitrary caller side effects, and
		// a canceled open must not run them for a session that will never exist — nor track an app for
		// disposal that nothing would ever trigger, since no session would be created to release it.
		cancellationToken.ThrowIfCancellationRequested();
		descriptor ??= new SessionDescriptor();
		var app = _appFactory();
		_apps.TryAdd(app, 0);
		var handle = await ReplSessionHandle.StartAsync(
			this,
			app,
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
	/// Disposes every session this host still owns, then the root <see cref="IServiceProvider"/> of
	/// every distinct <see cref="ReplApp"/> a session was opened against. Disposing twice is a no-op.
	/// </summary>
	/// <remarks>
	/// The app's provider is disposed here, and only here: <see cref="ReplApp"/> exposes no disposal
	/// surface of its own — 661 call sites of <see cref="ReplApp.Create(Action{Microsoft.Extensions.DependencyInjection.IServiceCollection}?)"/>
	/// across this repository, none in a <c>using</c>, plus an app registers itself as a singleton in
	/// its own container, so giving it one would create a self-referential disposal cycle everywhere
	/// else it is used. This host is the one place that both builds an app's provider (through
	/// <c>ReplSessionHandle.StartAsync</c>) and knows every one it built, so it is the one place that
	/// can own releasing them, without changing anything for a caller outside <c>Repl.Testing</c>.
	/// Apps come after sessions: a session's DI scope is a CHILD of its app's root provider.
	/// </remarks>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		// Captured before disposing: ReplSessionHandle.DisposeAsync can defer releasing its DI scope to
		// a command that was still running when it was called (see ReplSessionHandle), and this list is
		// what lets the app-root loop below tell which apps that affects, after _sessions is cleared.
		var sessions = _sessions.Values.ToArray();
		foreach (var session in sessions)
		{
			await session.DisposeAsync().ConfigureAwait(false);
		}

		_sessions.Clear();

		foreach (var app in _apps.Keys)
		{
			// An app whose session deferred its own scope release to a command still in flight must not
			// have its root disposed here either: that command's scope is a child of this root, and
			// disposing the root out from under it is the same hazard ReplSessionHandle.DisposeAsync
			// itself refuses to create. Never WAIT for that command the way it never does — leaving the
			// root undisposed alongside the deferred scope is the lesser harm; the process reclaims both.
			if (Array.Exists(sessions, s => ReferenceEquals(s.App, app) && !s.ServicesDisposed.IsCompleted))
			{
				continue;
			}

			switch (app.Services)
			{
				case IAsyncDisposable asyncDisposable:
					await asyncDisposable.DisposeAsync().ConfigureAwait(false);
					break;
				case IDisposable disposable:
					disposable.Dispose();
					break;
			}
		}

		_apps.Clear();
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
