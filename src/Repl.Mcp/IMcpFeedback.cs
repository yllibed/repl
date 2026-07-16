using ModelContextProtocol.Protocol;
using Repl.Interaction;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005) with no replacement API; hosts still rely on them, so Repl keeps
// supporting the features until the SDK removes them. Tracked in issue #51.
#pragma warning disable MCP9005

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
	/// Gets a value indicating whether the connected MCP client can receive logging/message notifications.
	/// </summary>
	bool IsLoggingSupported { get; }

	/// <summary>
	/// Reports a structured progress update to the connected MCP client.
	/// </summary>
	ValueTask ReportProgressAsync(
		ReplProgressEvent progress,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Sends a structured MCP message notification to the connected client.
	/// </summary>
	ValueTask SendMessageAsync(
		LoggingLevel level,
		object? data,
		CancellationToken cancellationToken = default);
}
