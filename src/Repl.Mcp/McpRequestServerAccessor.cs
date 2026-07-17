using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Resolves the <see cref="McpServer"/> a capability call must target.
/// </summary>
/// <remarks>
/// SDK 2.0's <c>2026-07-28</c> protocol path hands each request a destination-bound
/// <see cref="McpServer"/>, and one handler can serve several sessions. The capability
/// services are singletons (exposed through DI to command handlers), so the effective
/// server must be the one bound to the FLOWING request — a shared mutable field would be
/// overwritten by whichever request attached last, cross-wiring capabilities between
/// concurrent calls. <see cref="AsyncLocal{T}"/> flows with the invocation and cannot
/// leak across requests; the session-level server remains the fallback for code running
/// outside a request (e.g. routing-change notifications).
/// </remarks>
internal sealed class McpRequestServerAccessor
{
	private readonly AsyncLocal<McpServer?> _current = new();
	private McpServer? _session;

	/// <summary>Server for the flowing request, falling back to the session server.</summary>
	public McpServer? Effective => _current.Value ?? _session;

	/// <summary>Binds the flowing async context to the request's destination server.</summary>
	public void BindRequest(McpServer server) => _current.Value = server;

	/// <summary>Records the session-level server used outside request flows (null when the last session ends).</summary>
	public void AttachSession(McpServer? server) => _session = server;
}
