using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// State owned by ONE MCP transport session.
/// </summary>
/// <remarks>
/// One <see cref="McpServerHandler"/> can serve several concurrent sessions, so anything
/// that varies per client lives here instead of on the handler: hard/soft roots, the
/// generated snapshot cache (the tool graph can be gated on session capabilities), the
/// compatibility-shim intro state, and the session's service overlay. The context is
/// registered in the provider passed to <c>McpServer.Create</c>, so request handlers
/// recover their originating session through <c>request.Server.Services</c> — never
/// through a destination-bound per-request server used as a surrogate session key.
/// Request-bound OUTBOUND capabilities (sampling, elicitation, progress) keep flowing
/// through the per-request <see cref="McpRequestServerAccessor"/> binding, which is
/// finer-grained than the session.
/// </remarks>
internal sealed class McpSessionContext
{
	private SnapshotCacheEntry? _snapshotCache;
	private int _compatibilityIntroServed;

	public McpSessionContext(McpClientRootsService roots, IServiceProvider services)
	{
		Roots = roots;
		Services = services;
	}

	/// <summary>Session-owned hard/soft roots.</summary>
	public McpClientRootsService Roots { get; }

	/// <summary>Per-session service overlay handed to <c>McpServer.Create</c>.</summary>
	public IServiceProvider Services { get; }

	/// <summary>Session server used for server-initiated notifications.</summary>
	public McpServer? SessionServer { get; set; }

	/// <summary>Serializes snapshot builds for this session.</summary>
	public SemaphoreSlim SnapshotGate { get; } = new(initialCount: 1, maxCount: 1);

	/// <summary>
	/// Cached snapshot paired with the routing version it was built at, or <see langword="null"/>
	/// before this session's first build.
	/// </summary>
	public SnapshotCacheEntry? SnapshotCache => Volatile.Read(ref _snapshotCache);

	/// <summary>Publishes <paramref name="snapshot"/> as current for <paramref name="version"/>.</summary>
	public void PublishSnapshot(McpServerHandler.McpGeneratedSnapshot snapshot, long version) =>
		Volatile.Write(ref _snapshotCache, new SnapshotCacheEntry(snapshot, version));

	/// <summary>
	/// Publishes <paramref name="snapshot"/> as serve-able but stale, so the next request rebuilds
	/// without waiting for another routing mutation.
	/// </summary>
	public void PublishStaleSnapshot(McpServerHandler.McpGeneratedSnapshot snapshot) =>
		Volatile.Write(ref _snapshotCache, new SnapshotCacheEntry(snapshot, SnapshotCacheEntry.StaleVersion));

	/// <summary>
	/// Claims this session's one-time compatibility-shim intro; <see langword="true"/> for the first
	/// caller only.
	/// </summary>
	public bool TryClaimCompatibilityIntro() =>
		Interlocked.CompareExchange(ref _compatibilityIntroServed, 1, 0) == 0;

	/// <summary>Re-arms the compatibility-shim intro after a routing invalidation.</summary>
	public void ResetCompatibilityIntro() => Interlocked.Exchange(ref _compatibilityIntroServed, 0);

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
	internal sealed record SnapshotCacheEntry(McpServerHandler.McpGeneratedSnapshot Snapshot, long Version)
	{
		/// <summary>
		/// Marks an entry serve-able but stale. Routing versions start at 1 and only increase, so this
		/// can never equal a live version and the next request always rebuilds.
		/// </summary>
		public const long StaleVersion = 0;
	}
}
