namespace Repl;

/// <summary>
/// Controls whether standalone runs translate process termination signals into cooperative cancellation.
/// </summary>
public enum ProcessSignalHandlingMode
{
	/// <summary>
	/// Process signal handling remains the responsibility of the caller. This is the zero value so that
	/// an unset configuration field or a zero-initialized value agrees with the caller-owned application
	/// default instead of silently claiming process-wide signal ownership.
	/// </summary>
	None = 0,

	/// <summary>
	/// Standalone runs handle Ctrl+C console events, plus Ctrl+Break on Windows, for their duration.
	/// They also handle SIGTERM on supported Unix platforms. The first such signal cancels the handler's
	/// token so cleanup can run, and the run then reports <c>130</c> for Ctrl+C or Ctrl+Break and
	/// <c>143</c> for SIGTERM, unless the handler returned a non-zero exit code of its own, which wins.
	/// A second signal is left to the operating system. Interactive sessions are unaffected: inside an
	/// interactive command, Ctrl+C keeps cancelling only that command.
	/// </summary>
	Automatic = 1,
}
