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
	/// Gets a value indicating whether a message sent for the current request would reach the
	/// connected MCP client as a notification.
	/// </summary>
	/// <remarks>
	/// On the <c>2026-07-28</c> revision this is <see langword="false"/> unless the request declared
	/// a log level in its metadata, because the specification forbids emitting message notifications
	/// for a request that did not ask for them. A message sent while this is <see langword="false"/>
	/// is not lost: it is carried back in the tool result instead.
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
	/// otherwise as part of the tool result.
	/// </summary>
	ValueTask SendMessageAsync(
		McpMessageLevel level,
		object? data,
		CancellationToken cancellationToken = default);
}
