namespace Repl;

/// <summary>
/// Shared ANSI gate for terminal-sequence emitters (shell-integration marks, advanced
/// progress). A hosted client can advertise ANSI purely through capability flags
/// (identity inference, control messages, <c>TerminalSessionOverrides</c>) without ever
/// setting the <c>AnsiSupport</c> override; honoring that flag avoids falling back to
/// <see cref="OutputOptions.IsAnsiEnabled"/>'s view of the server console's redirection
/// state. Explicit opt-outs (<c>AnsiSupport=false</c>, <see cref="AnsiMode.Never"/>)
/// still win.
/// </summary>
internal static class TerminalAnsiCapability
{
	public static bool IsAnsiCapableForTerminalSequences(OutputOptions outputOptions)
	{
		if (outputOptions.IsAnsiEnabled())
		{
			return true;
		}

		// The fallback only bypasses the server console's own state (redirection, host
		// detection) — never the explicit opt-outs: AnsiSupport=false and AnsiMode.Never
		// below, and the NO_COLOR / TERM=dumb escape hatches the docs promise to honor.
		return ReplSessionIO.IsSessionActive
			&& ReplSessionIO.AnsiSupport is null
			&& outputOptions.AnsiMode != AnsiMode.Never
			&& !TerminalEnvironmentClassifier.IsAnsiOptOutEnvironment()
			&& ReplSessionIO.TerminalCapabilities.HasFlag(TerminalCapabilities.Ansi);
	}
}
