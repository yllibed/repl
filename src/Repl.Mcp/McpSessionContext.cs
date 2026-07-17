using ModelContextProtocol.Server;
using Repl.Documentation;

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

	/// <summary>Cached generated snapshot for this session.</summary>
	public McpServerHandler.McpGeneratedSnapshot? Snapshot { get; set; }

	/// <summary>Routing version the cached snapshot was built at.</summary>
	public long BuiltSnapshotVersion { get; set; }

	/// <summary>Whether this session already received the compatibility-shim intro list.</summary>
	public int CompatibilityIntroServed;
}
