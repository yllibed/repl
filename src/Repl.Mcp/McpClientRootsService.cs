using System.Diagnostics;
using System.Runtime.CompilerServices;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005); kept for existing hosts.
// Rationale and successor: docs/mcp-reference.md#sdk-and-protocol-versions (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

// Caches native roots for exactly as long as its McpRootsScope allows, and no longer. Outbound
// transport goes through the request-bound accessor under either scope: the destination is per
// request, which is finer than a session.
internal sealed class McpClientRootsService : IMcpClientRoots
{
	private readonly ICoreReplApp _app;
	private readonly McpRequestServerAccessor _servers;
	private readonly McpRootsScope _scope;
	// 1 while a failure to prime is being reported at Debug instead of Warning; reset by a successful
	// prime. Interlocked, since concurrent tool calls on one connection prime concurrently.
	private int _primeFailureReported;
	private readonly Lock _syncRoot = new();
	// Bounds the one outbound call this type makes. Request scope pays it per request rather than once
	// per connection, so a client that never answers roots/list would otherwise hold every tool call
	// that resolves roots open with nothing to stop it.
	private static readonly TimeSpan RootsRequestBudget = TimeSpan.FromSeconds(10);
	// How long the eager prime stands down after an expensive failure. A client that declares the roots
	// capability and then never answers would otherwise cost the full budget above on every single
	// execution, for the life of the connection: nothing caches a failure, and the shared attempt is
	// retracted at the next acquisition precisely so the next caller retries. That retry is right for a
	// handler that asks for roots and wrong for the prime, which asks on nobody's behalf.
	private static readonly TimeSpan PrimeRetryCooldown = TimeSpan.FromSeconds(30);

	// How many times one call will ask before giving up. A roots/list_changed processed while a fetch is
	// unanswered retires that fetch's answer, and the caller must not be handed it: the client has said
	// those roots no longer apply, and nothing in the value distinguishes it from a current one. Asking
	// again is the only way to honour the call, and the count bounds it because the client decides how
	// often it invalidates.
	private const int MaxRootsFetchAttempts = 3;

	// Only a failure that actually spent the budget is worth standing down for. A client that answers
	// roots/list promptly with something unusable costs nothing to ask again, and backing off there
	// would just hold Current empty for half a minute after a fault that may already have cleared.
	private static readonly TimeSpan PrimeStandDownThreshold = TimeSpan.FromMilliseconds(
		RootsRequestBudget.TotalMilliseconds / 2);

	// Request scope only. Keyed by the flowing request, so entries die with it and nothing here ever
	// needs invalidating.
	private readonly ConditionalWeakTable<MessageContext, RequestRoots> _requestRoots = new();
	// Connection scope only.
	private McpClientRoot[] _hardRoots = [];
	private McpClientRoot[] _softRoots = [];
	private bool _hardRootsLoaded;
	private Task<IReadOnlyList<McpClientRoot>>? _hardRootsPending;
	private long _hardRootsVersion;
	// When the eager prime last failed, so it can stop paying the full budget on every execution.
	private long? _primeFailedAt;

	public McpClientRootsService(ICoreReplApp app, McpRequestServerAccessor servers, McpRootsScope scope)
	{
		_app = app;
		_servers = servers;
		_scope = scope;
	}

	public bool IsSupported => _servers.Effective?.ClientCapabilities?.Roots is not null;

	public bool HasSoftRoots
	{
		get
		{
			lock (_syncRoot)
			{
				return _softRoots.Length > 0;
			}
		}
	}

	public IReadOnlyList<McpClientRoot> Current
	{
		get
		{
			if (_scope is McpRootsScope.Request)
			{
				// Only what this request already resolved. Answering with another connection's cached
				// roots is the same disclosure as GetAsync's, reached without any round-trip at all.
				// One read of the flowing request, like GetAsync below.
				if (_servers.Current is not { } request)
				{
					return GetSoftRoots();
				}

				return _requestRoots.TryGetValue(request, out var entry) && entry.Resolved is { } resolved
					? resolved
					: request.Server.ClientCapabilities?.Roots is not null ? [] : GetSoftRoots();
			}

			lock (_syncRoot)
			{
				// Native roots stand in for soft ones only once they have actually been resolved. Until
				// then — never primed, or primed and failed — _hardRoots is empty, and answering with it
				// would report "this client declared no roots" for what is really "nobody could ask it",
				// which is the reading a handler is most likely to act on and the one it cannot check.
				// A client that genuinely answers with zero roots sets _hardRootsLoaded, so that case is
				// still told apart from this one.
				return IsSupported && _hardRootsLoaded ? _hardRoots : _softRoots;
			}
		}
	}

	/// <summary>
	/// Resolves this connection's native roots so that <see cref="Current"/> answers with them without
	/// the caller having to ask first. A no-op outside connection scope.
	/// </summary>
	/// <remarks>
	/// Called at the command execution boundary. Connection scope is where <see cref="Current"/> promises
	/// the session's roots, so a handler reading it must not have to prime the cache itself; the answer is
	/// then cached for the life of the connection, which is one <c>roots/list</c> — the same cost as the
	/// discovery-time pre-resolution this replaces. Request scope is deliberately excluded: there
	/// <see cref="Current"/> is documented as only what this request already resolved, and an eager fetch
	/// would add a round-trip to every request rather than to every connection.
	/// </remarks>
	/// <returns>
	/// <see langword="true"/> only when roots were actually fetched; <see langword="false"/> when the
	/// prime did not ask at all — outside connection scope, without client support, or standing down.
	/// </returns>
	internal async ValueTask<bool> PrimeCurrentAsync(CancellationToken cancellationToken)
	{
		if (_scope is not McpRootsScope.Connection || !IsSupported)
		{
			return false;
		}

		lock (_syncRoot)
		{
			// Standing down is only ever right while there is still nothing cached; once a fetch has
			// succeeded GetAsync answers from the cache and costs nothing to call.
			if (!_hardRootsLoaded
				&& _primeFailedAt is { } failedAt
				&& Stopwatch.GetElapsedTime(failedAt) < PrimeRetryCooldown)
			{
				return false;
			}
		}

		var startedAt = Stopwatch.GetTimestamp();
		long versionAtStart;
		lock (_syncRoot)
		{
			versionAtStart = _hardRootsVersion;
		}

		try
		{
			await GetAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception)
			when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			if (Stopwatch.GetElapsedTime(startedAt) >= PrimeStandDownThreshold)
			{
				lock (_syncRoot)
				{
					// Not if the client said its roots changed while this attempt was running. The
					// notification clears the stand-down on purpose — whatever made the attempt fail may be
					// exactly what it is reporting — and an attempt that began before it must not put the
					// stand-down back afterwards, which is what a slow failure would otherwise do. Then no
					// execution would prime for half a minute despite the client having asked for it.
					if (_hardRootsVersion == versionAtStart)
					{
						_primeFailedAt = Stopwatch.GetTimestamp();
					}
				}
			}

			throw;
		}

		lock (_syncRoot)
		{
			_primeFailedAt = null;
		}

		return true;
	}

	public async ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default)
	{
		// Single read: the effective server must not change between the support check and
		// the roots request (a concurrent request re-binding the accessor must not be observed).
		if (_servers.Current is not { } request || request.Server.ClientCapabilities?.Roots is null)
		{
			return Current;
		}

		var server = request.Server;
		if (_scope is McpRootsScope.Request)
		{
			// The table hands every caller in this request the same entry; the entry, not the table's
			// factory, starts the fetch. ConditionalWeakTable.GetValue documents that it may run its
			// factory more than once for one key and outside its own lock, so a factory that started
			// work would issue a second roots/list and orphan one of the two results.
			var entry = _requestRoots.GetValue(request, static _ => new RequestRoots());
			return await entry.ResolveAsync(server, cancellationToken).ConfigureAwait(false);
		}

		for (var attempt = 1; ; attempt++)
		{
			Task<IReadOnlyList<McpClientRoot>> pending;
			lock (_syncRoot)
			{
				if (_hardRootsLoaded)
				{
					return _hardRoots;
				}

				var version = _hardRootsVersion;
				pending = JoinOrStartAsync(ref _hardRootsPending, () => FetchHardRootsOnceAsync(server, version));
			}

			// Waited on this caller's token while the fetch runs on its own budget, the same shape request
			// scope uses: concurrent first calls share one roots/list, and a caller giving up releases only
			// itself. The answer is read back from the cache rather than from the task, because whether it
			// was cached is exactly what says it is still current.
#pragma warning disable VSTHRD003 // Started by this instance, a few lines above.
			await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003

			lock (_syncRoot)
			{
				if (_hardRootsLoaded)
				{
					return _hardRoots;
				}
			}

			// Nothing cached means the version moved while that fetch was unanswered, so the answer it
			// carries was retracted before it arrived. Refusing to cache it is not enough: handing it back
			// gives this caller roots the client has withdrawn, and nothing in the value says so.
			if (attempt >= MaxRootsFetchAttempts)
			{
				throw new McpException("Client roots changed repeatedly while they were being resolved.");
			}
		}
	}

	public void SetSoftRoots(IEnumerable<McpClientRoot> roots)
	{
		ArgumentNullException.ThrowIfNull(roots);

		var normalized = roots.ToArray();
		var changed = false;
		lock (_syncRoot)
		{
			if (!AreEqual(_softRoots, normalized))
			{
				_softRoots = normalized;
				changed = true;
			}
		}

		if (changed)
		{
			_app.InvalidateRouting();
		}
	}

	public void ClearSoftRoots()
	{
		var changed = false;
		lock (_syncRoot)
		{
			if (_softRoots.Length > 0)
			{
				_softRoots = [];
				changed = true;
			}
		}

		if (changed)
		{
			_app.InvalidateRouting();
		}
	}

	// Reached only under connection scope: the notification handler is registered from AttachSession,
	// which never runs on the path that builds a request-scoped service. Clearing the connection fields
	// is harmless either way, since request scope never writes them.
	public void HandleRootsListChanged()
	{
		lock (_syncRoot)
		{
			_hardRoots = [];
			_hardRootsLoaded = false;
			_hardRootsVersion++;

			// Retired with the array it produced, and under the same lock. The task is kept to coalesce
			// concurrent first calls; leaving a COMPLETED one here would make the next execution replay
			// the pre-notification answer and send no roots/list at all, so the cache would be cleared
			// with nothing left to refill it. A fetch still in flight is abandoned rather than awaited:
			// it started before the change and the version check already stops it caching.
			_hardRootsPending = null;

			// The client has just said something changed, which is the one signal worth interrupting the
			// prime's stand-down for: whatever made the last attempt fail may be what it is reporting.
			_primeFailedAt = null;
		}

		_app.InvalidateRouting();
	}

	private McpClientRoot[] GetSoftRoots()
	{
		lock (_syncRoot)
		{
			return _softRoots;
		}
	}

	private static async Task<McpClientRoot[]> FetchRootsAsync(
		McpServer server,
		CancellationToken cancellationToken)
	{
		var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
			.ConfigureAwait(false);
		return result.Roots?.Select(MapRoot).ToArray() ?? [];
	}

	/// <summary>
	/// Joins the attempt already outstanding in <paramref name="pending"/>, or starts one when there is
	/// none left to join.
	/// </summary>
	/// <remarks>
	/// Both scopes coalesce their concurrent first callers onto a single <c>roots/list</c>, and the rule
	/// for doing it is subtle enough that keeping two copies of it has cost this branch three rounds of
	/// fixing one and missing the other. A failed attempt is retracted here, at acquisition, rather than
	/// where a waiter observes the failure: the last waiter can abandon its wait while the attempt is
	/// still running, and then nothing is left to retract it when it faults afterwards. The fault is
	/// spoken for on the way out for that same reason. Callers hold their own lock across this, which is
	/// what makes the decision atomic against whatever else that lock guards, and each then waits on its
	/// own token so that one caller giving up releases only itself.
	/// </remarks>
	private static Task<T> JoinOrStartAsync<T>(ref Task<T>? pending, Func<Task<T>> start)
	{
		if (pending is { IsCompleted: true } settled && !settled.IsCompletedSuccessfully)
		{
			pending = null;
		}

		if (pending is null)
		{
			pending = start();
			ObserveFault(pending);
		}

		return pending;
	}

	/// <summary>
	/// Speaks for <paramref name="fetch"/>'s fault, so that nobody has to.
	/// </summary>
	/// <remarks>
	/// Every waiter leaves on its own token, so a shared fetch can fault with nobody left to read it,
	/// and it is then dropped unread — replaced at the next acquisition, retired by
	/// <see cref="HandleRootsListChanged"/>, or simply collected when the connection ends. Without this
	/// the exception reaches <see cref="TaskScheduler.UnobservedTaskException"/> from a finalizer,
	/// long after the request that caused it, and a host configured to throw on that crashes. The idiom
	/// matches <c>ReplProcessSignalHarness</c>'s.
	/// </remarks>
	private static void ObserveFault(Task fetch) =>
		_ = fetch.ContinueWith(
			static observed => _ = observed.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

	/// <summary>
	/// The one outstanding connection-scoped fetch, shared by every caller that arrives before it
	/// completes.
	/// </summary>
	/// <remarks>
	/// Without this, two first invocations on one connection each send their own <c>roots/list</c> and
	/// one result is discarded, which the "one round-trip per connection" cost claim does not allow. It
	/// carries its own budget for the same reason request scope does: the result belongs to every
	/// caller, so no single caller's token may bound it.
	/// </remarks>
	private async Task<IReadOnlyList<McpClientRoot>> FetchHardRootsOnceAsync(McpServer server, long versionAtStart)
	{
		using var budget = new CancellationTokenSource(RootsRequestBudget);
		return await GetAndMaybeCacheRootsAsync(server, versionAtStart, budget.Token).ConfigureAwait(false);
	}

	/// <summary>
	/// Primes the connection's native roots from <paramref name="services"/>, logging and swallowing a
	/// failure.
	/// </summary>
	/// <remarks>
	/// Called from every execution entry point. A handler that never reads roots must not fail because
	/// the client could not answer, and one that does read them surfaces the error from its own
	/// <see cref="GetAsync"/>. Cancellation is the caller's and propagates.
	/// </remarks>
	internal static async ValueTask PrimeFromServicesAsync(
		IServiceProvider services,
		CancellationToken cancellationToken)
	{
		if (services.GetService(typeof(IMcpClientRoots)) is not McpClientRootsService roots)
		{
			return;
		}

		try
		{
			// Only a prime that actually asked ends the episode: one skipped while standing down says
			// nothing about whether the client can answer yet.
			if (await roots.PrimeCurrentAsync(cancellationToken).ConfigureAwait(false))
			{
				Interlocked.Exchange(ref roots._primeFailureReported, 0);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Swallowed for the command (see the remarks above), not for the operator. Warned once per
			// failure episode: a fast failure is retried on every tool call, where the prime cooldown only
			// covers one that exhausted its budget. Built only here, so a successful prime pays nothing.
			var diagnostics = new McpLoggerDiagnostics(services);
			if (Interlocked.Exchange(ref roots._primeFailureReported, 1) == 0)
			{
				diagnostics.RootsPrimeFailed(ex);
			}
			else
			{
				diagnostics.RootsPrimeStillFailing(ex);
			}
		}
	}

	private async ValueTask<IReadOnlyList<McpClientRoot>> GetAndMaybeCacheRootsAsync(
		McpServer server,
		long versionAtStart,
		CancellationToken cancellationToken)
	{
		var mappedRoots = await FetchRootsAsync(server, cancellationToken).ConfigureAwait(false);

		// Caching is refused for an answer whose version was retired while it was still outstanding: it
		// would pin roots the client has already retracted, with nothing left to refill them. The refusal
		// is also the signal GetAsync reads — an uncached answer is a retracted one, and that caller asks
		// again rather than receive it. The returned value is therefore only meaningful when it was
		// cached. Ordering a roots/list_changed against an unanswered fetch is awkward but not impossible:
		// the guard holds the answer until the server echoes tools/list_changed, which it emits from the
		// same handler that moves the version.
		lock (_syncRoot)
		{
			if (_hardRootsVersion == versionAtStart)
			{
				_hardRoots = mappedRoots;
				_hardRootsLoaded = true;
				return _hardRoots;
			}

			return mappedRoots;
		}
	}

	/// <summary>
	/// One request's native roots: fetched at most once, and forgotten with the request.
	/// </summary>
	private sealed class RequestRoots
	{
		private readonly Lock _gate = new();
		private Task<McpClientRoot[]>? _pending;
		private McpClientRoot[]? _resolved;

		/// <summary>What this request has settled on, or <see langword="null"/> while it has not.</summary>
		public McpClientRoot[]? Resolved => Volatile.Read(ref _resolved);

		public async Task<IReadOnlyList<McpClientRoot>> ResolveAsync(
			McpServer server,
			CancellationToken cancellationToken)
		{
			Task<McpClientRoot[]> pending;
			lock (_gate)
			{
				pending = JoinOrStartAsync(ref _pending, () => FetchOnceAsync(server));
			}

			// Waited on this caller's token while the fetch itself runs on its own budget: one caller
			// giving up must release that caller, and must not cancel the result the others share.
#pragma warning disable VSTHRD003 // Started by this instance, for this request, one line above.
			return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}

		private async Task<McpClientRoot[]> FetchOnceAsync(McpServer server)
		{
			// Its own budget rather than a caller's token: the result is shared by every caller in this
			// request, so cancelling one must not cancel the others, and nothing else bounds the wait.
			using var budget = new CancellationTokenSource(RootsRequestBudget);
			var roots = await FetchRootsAsync(server, budget.Token).ConfigureAwait(false);
			Volatile.Write(ref _resolved, roots);
			return roots;
		}
	}

	private static McpClientRoot MapRoot(Root root)
	{
		var uri = Uri.TryCreate(root.Uri, UriKind.Absolute, out var parsed)
			? parsed
			: new Uri(root.Uri, UriKind.RelativeOrAbsolute);
		return new McpClientRoot(uri, root.Name);
	}

	private static bool AreEqual(McpClientRoot[] left, McpClientRoot[] right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}

		if (left.Length != right.Length)
		{
			return false;
		}

		for (var i = 0; i < left.Length; i++)
		{
			if (left[i] != right[i])
			{
				return false;
			}
		}

		return true;
	}
}
