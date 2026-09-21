using Microsoft.Extensions.DependencyInjection;

namespace Repl.Mcp;

/// <summary>
/// State owned by one MCP transport session, or — on the reusable-options path — by the handler
/// itself, standing in for a session that the protocol no longer provides.
/// </summary>
/// <remarks>
/// One <see cref="McpServerHandler"/> can serve several concurrent sessions, so anything
/// that varies per client lives here instead of on the handler: hard/soft roots, the
/// generated snapshot cache (the tool graph can be gated on session capabilities), the
/// compatibility-shim intro state, the session's DI scope, and its service overlay. The context is
/// registered in the provider passed to <c>McpServer.Create</c>, so request handlers
/// recover their originating session through <c>request.Server.Services</c> — never
/// through a destination-bound per-request server used as a surrogate session key.
/// Request-bound OUTBOUND capabilities (sampling, elicitation, progress) keep flowing
/// through the per-request <see cref="McpRequestServerAccessor"/> binding, which is
/// finer-grained than the session.
/// </remarks>
internal sealed class McpSessionContext : IDisposable
{
	private SnapshotCacheEntry? _snapshotCache;
	private int _compatibilityIntroServed;

	private readonly AsyncServiceScope? _scope;

	public McpSessionContext(
		McpClientRootsService roots,
		IServiceProvider services,
		AsyncServiceScope? scope)
	{
		Roots = roots;
		Services = services;
		_scope = scope;
	}

	/// <summary>Session-owned hard/soft roots.</summary>
	public McpClientRootsService Roots { get; }

	/// <summary>Per-session service overlay handed to <c>McpServer.Create</c>.</summary>
	public IServiceProvider Services { get; }

	/// <summary>Serializes snapshot builds for this session.</summary>
	public SemaphoreSlim SnapshotGate { get; } = new(initialCount: 1, maxCount: 1);

	/// <summary>
	/// Cached snapshot paired with the routing version it was built at, or <see langword="null"/>
	/// before this session's first build.
	/// </summary>
	public SnapshotCacheEntry? SnapshotCache => Volatile.Read(ref _snapshotCache);

	/// <summary>Publishes <paramref name="snapshot"/> as current for <paramref name="version"/>.</summary>
	public void PublishSnapshot(
		McpServerHandler.McpGeneratedSnapshot snapshot,
		long version,
		bool sessionless) =>
		Volatile.Write(ref _snapshotCache, new SnapshotCacheEntry(snapshot, version, IsStale: false, sessionless));

	/// <summary>
	/// Publishes <paramref name="snapshot"/>, built at <paramref name="version"/>, as serve-able but
	/// stale, so the next request rebuilds without waiting for another routing mutation.
	/// </summary>
	public void PublishStaleSnapshot(
		McpServerHandler.McpGeneratedSnapshot snapshot,
		long version,
		bool sessionless) =>
		Volatile.Write(ref _snapshotCache, new SnapshotCacheEntry(snapshot, version, IsStale: true, sessionless));

	/// <summary>
	/// Claims this session's one-time compatibility-shim intro; <see langword="true"/> for the first
	/// caller only.
	/// </summary>
	public bool TryClaimCompatibilityIntro() =>
		Interlocked.CompareExchange(ref _compatibilityIntroServed, 1, 0) == 0;

	/// <summary>Re-arms the compatibility-shim intro after a routing invalidation.</summary>
	public void ResetCompatibilityIntro() => Interlocked.Exchange(ref _compatibilityIntroServed, 0);

	public void Dispose()
	{
		SnapshotGate.Dispose();
		// Releases this session's Scoped services. Disposed synchronously because the context is,
		// which is the reason the scope is stored rather than awaited: nothing on an MCP connection
		// teardown path is async, and a Scoped disposable here is an application object, not I/O.
		_scope?.Dispose();
	}

	/// <summary>
	/// A generated snapshot and the routing version it was built at, published as ONE value.
	/// </summary>
	/// <remarks>
	/// Held as two independent fields, a lock-free reader could observe the fresh version paired with
	/// the previous snapshot and serve stale discovery state; one reference swapped with
	/// release/acquire semantics removes the ordering question altogether. Writers are serialized by
	/// <see cref="SnapshotGate"/>, so a plain <c>Volatile.Write</c> suffices — unlike
	/// <see cref="McpServerHandler.PublishSnapshotInvalidation"/>, which races several threads and
	/// therefore needs a compare-and-swap loop.
	/// </remarks>
	/// <param name="Snapshot">The generated tool, resource and prompt graph.</param>
	/// <param name="Version">The routing version this snapshot was built at.</param>
	/// <param name="IsStale">Whether the next request must rebuild rather than serve this again.</param>
	/// <param name="Sessionless">
	/// The protocol era this snapshot was built for. It is part of the key rather than an attribute,
	/// because the two eras see different command graphs: a modern build answers every capability
	/// question "supported" so the advertised set cannot vary per connection, while a legacy build
	/// reflects the session's own capabilities. One connection really does reach this cache with both:
	/// the SDK accepts a modern per-request call and then an <c>initialize</c> handshake on the same
	/// pipe, which is what the specification means by a dual-era server. Without the era in the key the
	/// second request is served the first one's catalog — see
	/// <c>When_OneConnectionIsServedBothEras_Then_EachGetsItsOwnCatalog</c>.
	/// </param>
	internal sealed record SnapshotCacheEntry(
		McpServerHandler.McpGeneratedSnapshot Snapshot,
		long Version,
		bool IsStale,
		bool Sessionless);
}
