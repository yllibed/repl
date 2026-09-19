using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Provides direct access to MCP progress and message notifications from the connected client.
/// Inject this interface into command handlers when you need MCP-specific runtime feedback beyond
/// the portable <see cref="IReplInteractionChannel"/> abstraction.
/// </summary>
public interface IMcpFeedback
{
	/// <summary>
	/// Gets a value indicating whether the connected MCP client can receive progress updates
	/// for the current tool invocation.
	/// </summary>
	bool IsProgressSupported { get; }

	/// <summary>
	/// Gets a value indicating whether the current request has a severity threshold at all, so a
	/// message at or above it would reach the connected MCP client as a notification.
	/// </summary>
	/// <remarks>
	/// On the <c>2026-07-28</c> revision this is <see langword="false"/> unless the request declared
	/// a log level in its metadata, because the specification forbids emitting message notifications
	/// for a request that did not ask for them. A message sent while this is <see langword="false"/>
	/// during a <b>tool call</b> is not lost: it is carried back in the tool result instead. A resource
	/// read has no such place to put it — a resource body must match its advertised MIME type — so a
	/// message reported from a command serving a resource is dropped when the read succeeds. A read that
	/// fails has no body at all, and carries the message in the surfaced error instead.
	/// <para>
	/// A threshold existing is not a promise that every message arrives. An initialize-era host that
	/// asked for <c>Error</c> leaves this <see langword="true"/> while anything below that level is
	/// dropped — and dropped messages are <em>not</em> carried back in the tool result, because the
	/// client asked not to receive them.
	/// </para>
	/// </remarks>
	bool IsLoggingSupported { get; }

	/// <summary>
	/// Reports a structured progress update to the connected MCP client.
	/// </summary>
	ValueTask ReportProgressAsync(
		ReplProgressEvent progress,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Sends a message to the connected client, as a notification when the request asked for one and
	/// otherwise as part of the result.
	/// </summary>
	/// <remarks>
	/// A resource read that succeeds is the one path that keeps only its body, whose MIME type it has
	/// already advertised, and drops what was buffered; a read that fails carries it in the surfaced
	/// error. Everywhere else an undeliverable message is appended to the result rather than lost.
	/// </remarks>
	ValueTask SendMessageAsync(
		McpMessageLevel level,
		object? data,
		CancellationToken cancellationToken = default);
}
