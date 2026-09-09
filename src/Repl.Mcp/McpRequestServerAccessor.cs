using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Resolves the <see cref="McpServer"/> a capability call must target, from the flowing request.
/// </summary>
/// <remarks>
/// On the <c>2026-07-28</c> revision there is no <c>initialize</c> handshake: the client declares its
/// capabilities per request in <c>_meta</c>, and the SDK surfaces them only on the destination-bound
/// server handed to a handler — <see cref="McpServer.ClientCapabilities"/> is documented as
/// <see langword="null"/> on the root server. Capability resolution is therefore a property of the
/// REQUEST, not of the connection.
/// <para>
/// The whole <see cref="MessageContext"/> is bound rather than just its server, because the same
/// per-request metadata carries more than the destination (see the log level in
/// <see cref="McpFeedbackService"/>). <see cref="AsyncLocal{T}"/> flows with the invocation and cannot
/// leak across requests. There is deliberately no session-level fallback: a shared field would hand
/// out the capabilities of whichever connection attached last, which is precisely the cross-wiring
/// this type exists to prevent.
/// </para>
/// </remarks>
internal sealed class McpRequestServerAccessor
{
	private readonly AsyncLocal<MessageContext?> _current = new();

	/// <summary>The request currently flowing on this async context, if any.</summary>
	public MessageContext? Current => _current.Value;

	/// <summary>Server for the flowing request, or <see langword="null"/> outside a request.</summary>
	public McpServer? Effective => _current.Value?.Server;

	/// <summary>Binds the flowing async context to the request being served.</summary>
	public void BindRequest(MessageContext request) => _current.Value = request;
}
