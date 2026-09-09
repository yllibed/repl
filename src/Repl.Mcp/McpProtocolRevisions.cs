namespace Repl.Mcp;

/// <summary>
/// MCP protocol revisions Repl reasons about explicitly.
/// </summary>
/// <remarks>
/// The SDK's own <c>McpProtocolVersions</c> class is internal, so the literals have to live here.
/// </remarks>
internal static class McpProtocolRevisions
{
	/// <summary>
	/// The last revision built on the <c>initialize</c> handshake, and therefore the last one with
	/// protocol-level sessions.
	/// </summary>
	public const string LastWithSessions = "2025-11-25";

	/// <summary>
	/// The revision that removed protocol sessions (SEP-2567) and moved per-call state — client
	/// capabilities, log level — into per-request <c>_meta</c> (SEP-2575).
	/// </summary>
	public const string Sessionless = "2026-07-28";
}
