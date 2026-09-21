namespace Repl.Mcp;

/// <summary>
/// How long a resolved set of native client roots may be kept.
/// </summary>
/// <remarks>
/// The <c>2026-07-28</c> revision removed protocol sessions, so the two hosting shapes this package
/// supports differ in what identity they can offer. <c>mcp serve</c> creates one context per
/// connection and can cache for that connection's lifetime; a host reusing one
/// <c>BuildMcpServerOptions()</c> result across connections has no per-connection identity at all —
/// the server handed to a handler is destination-bound and constructed per message — so the widest
/// honest scope there is the request.
/// </remarks>
internal enum McpRootsScope
{
	/// <summary>
	/// One MCP transport session. Roots are fetched once and kept until the client says they changed.
	/// </summary>
	Connection,

	/// <summary>
	/// One request. Roots are fetched at most once per request and never outlive it, which is what
	/// keeps one client's workspace from reaching another when connections share a service instance.
	/// </summary>
	Request,
}
