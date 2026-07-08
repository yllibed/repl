namespace Repl;

/// <summary>
/// Classifies the local terminal from environment variables. Shared by features that
/// emit terminal-specific escape sequences (advanced progress, shell-integration marks)
/// so detection heuristics stay in one place.
/// </summary>
internal static class TerminalEnvironmentClassifier
{
	public static bool IsTerminalMultiplexerSession()
	{
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMUX")))
		{
			return true;
		}

		var term = Environment.GetEnvironmentVariable("TERM");
		return term?.StartsWith("screen", StringComparison.OrdinalIgnoreCase) is true
			|| term?.StartsWith("tmux", StringComparison.OrdinalIgnoreCase) is true;
	}

	public static bool IsKnownAdvancedProgressTerminal()
	{
		if (IsTerminalMultiplexerSession())
		{
			return false;
		}

		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION"))
			|| string.Equals(Environment.GetEnvironmentVariable("ConEmuANSI"), "ON", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "WezTerm", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsKnownShellIntegrationTerminal()
	{
		if (IsTerminalMultiplexerSession())
		{
			return false;
		}

		// ConEmu is deliberately absent: it renders OSC 9;4 progress but not
		// FinalTerm/VS Code prompt marks.
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION"))
			|| IsVsCodeTerminal()
			|| string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "WezTerm", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsVsCodeTerminal() =>
		string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True when the environment explicitly opts out of ANSI output — the documented
	/// end-user escape hatches, with the same precedence styled output applies:
	/// NO_COLOR wins over everything, CLICOLOR_FORCE=1 overrides TERM=dumb. Shared by
	/// <c>OutputOptions.IsAnsiEnabled</c> and the hosted capability fallback
	/// (<c>TerminalAnsiCapability</c>) so the two gates cannot drift.
	/// </summary>
	public static bool IsAnsiOptOutEnvironment()
	{
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR")))
		{
			return true;
		}

		if (string.Equals(Environment.GetEnvironmentVariable("CLICOLOR_FORCE"), "1", StringComparison.Ordinal))
		{
			return false;
		}

		return string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
	}
}
