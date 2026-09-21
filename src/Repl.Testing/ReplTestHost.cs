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
	// Every session ever opened against an app contributes its ServicesDisposed task here, at open time
	// — independent of _sessions, which a session leaves as soon as ITS OWN DisposeAsync is called, even
	// while its command is still running and its scope release is deferred. Without this, a caller that
	// disposes a session handle directly (not through the host) before its command finishes makes that
	// session invisible to the app-root check below despite its scope still being live.
	private readonly ConcurrentDictionary<ReplApp, ConcurrentQueue<Task>> _pendingServiceDisposals =
		new(ReferenceEqualityComparer.Instance);
	// App-root disposals DisposeAsync scheduled rather than awaited, because a session's command was
	// still running at the time. Retained so a failure during one of them — a singleton's Dispose()
	// throwing — is observable through WaitForDeferredCleanupAsync instead of becoming an unobserved
	// task exception, since DisposeAsync itself has already returned success by the time these run.
	private readonly ConcurrentBag<Task> _deferredAppDisposals = new();
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
		// Tracked only once StartAsync actually succeeds, not right after the factory call: StartAsync
		// cannot fail once it has forced app.Services open (every throw in it — null checks, the
		// cancellation re-check, a bad descriptor — happens before that), so a failure here means
		// nothing Repl controls has touched the provider yet. Tracking (and, on the disposal race below,
		// disposing) it anyway would force it open for nothing, solely to immediately tear it back down.
		var handle = await ReplSessionHandle.StartAsync(
			this,
			app,
			descriptor,
			_options,
			cancellationToken).ConfigureAwait(false);

		// Re-checked after StartAsync ran — it and the factory above can both take arbitrarily long — so
		// a host disposed while this was in flight cannot let the app and session just produced escape
		// untracked past that disposal. Narrows the race rather than closing it outright: a concurrent
		// DisposeAsync could still finish its own iteration in the gap between this check and the
		// registrations below, though that gap is now a few in-memory operations instead of an
		// arbitrarily long factory call.
		if (_disposed)
		{
			await handle.DisposeAsync().ConfigureAwait(false);
			await DisposeAppAsync(app).ConfigureAwait(false);
			ThrowIfDisposed();
		}

		_apps.TryAdd(app, 0);
		// Recorded regardless of what happens to the handle afterward — including a caller disposing it
		// directly, which would otherwise remove it from _sessions before its deferred scope release runs.
		_pendingServiceDisposals.GetOrAdd(app, static _ => new ConcurrentQueue<Task>()).Enqueue(handle.ServicesDisposed);
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
		foreach (var session in _sessions.Values)
		{
			await session.DisposeAsync().ConfigureAwait(false);
		}

		_sessions.Clear();

		foreach (var app in _apps.Keys)
		{
			var pending = _pendingServiceDisposals.TryGetValue(app, out var queue)
				? Array.FindAll([.. queue], static t => !t.IsCompleted)
				: [];
			if (pending.Length > 0)
			{
				// Scheduled, not abandoned: disposing this root now would pull it out from under
				// whichever session's command is still running and holding a scope that descends from
				// it — the same hazard ReplSessionHandle.DisposeAsync itself refuses to create. Waiting
				// HERE would reintroduce, at the host level, the unbounded wait on a possibly-orphaned
				// command that design already rejected — so the disposal runs once every one of this
				// app's still-in-flight sessions actually finishes, whenever that turns out to be,
				// without this call blocking on it. Retained rather than discarded (see
				// _deferredAppDisposals) so a failure in it is observable instead of unobserved.
				_deferredAppDisposals.Add(DisposeAppOnceSessionsFinishAsync(app, pending));
				continue;
			}

			await DisposeAppAsync(app).ConfigureAwait(false);
		}

		_apps.Clear();
		_pendingServiceDisposals.Clear();
	}

	/// <summary>
	/// Awaits every app-root disposal <see cref="DisposeAsync"/> scheduled rather than performed
	/// directly, because a session's command was still running at the time it was called.
	/// </summary>
	/// <remarks>
	/// <see cref="DisposeAsync"/> itself never waits for these — doing so would hang for as long as an
	/// orphaned command does, exactly what that design avoids. Call this afterward only when a test
	/// needs to know deferred cleanup has actually finished, or to surface a cleanup failure that would
	/// otherwise never reach anything: <see cref="DisposeAsync"/> has already returned success by the
	/// time a deferred disposal runs, so nothing else observes it.
	/// </remarks>
	public Task WaitForDeferredCleanupAsync() => Task.WhenAll(_deferredAppDisposals);

	private static async Task DisposeAppOnceSessionsFinishAsync(ReplApp app, Task[] pending)
	{
		await Task.WhenAll(pending).ConfigureAwait(false);
		await DisposeAppAsync(app).ConfigureAwait(false);
	}

	private static async ValueTask DisposeAppAsync(ReplApp app)
	{
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

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
