namespace Repl.Mcp;

/// <summary>
/// Severity of a message sent to the connected MCP client through <see cref="IMcpFeedback"/>.
/// </summary>
/// <remarks>
/// This mirrors the protocol's syslog-derived severities. It exists as a Repl-owned type so the
/// public surface does not expose the SDK's <c>LoggingLevel</c>, which the <c>2026-07-28</c>
/// specification deprecates (SEP-2577, SDK diagnostic MCP9005): a consumer building with warnings
/// as errors would otherwise fail on a Repl signature it never chose to depend on.
/// </remarks>
public enum McpMessageLevel
{
	/// <summary>Detailed information, useful only when diagnosing a problem.</summary>
	Debug = 0,

	/// <summary>Normal operational information.</summary>
	Info = 1,

	/// <summary>A normal but significant condition.</summary>
	Notice = 2,

	/// <summary>A condition that is not an error but deserves attention.</summary>
	Warning = 3,

	/// <summary>An error that did not prevent the operation from continuing.</summary>
	Error = 4,

	/// <summary>A condition that requires immediate attention.</summary>
	Critical = 5,

	/// <summary>Action must be taken immediately.</summary>
	Alert = 6,

	/// <summary>The system is unusable.</summary>
	Emergency = 7,
}
