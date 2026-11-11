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
	/// What the environment says about colour: <c>true</c> forces it, <c>false</c> refuses it, <c>null</c> leaves
	/// the decision to the stream's redirection. In order: <c>FORCE_COLOR</c> with any non-empty value (even
	/// <c>0</c>, as in .NET 11's <c>System.Console</c>) forces it, then <see cref="IsAnsiOptOutEnvironment"/>
	/// refuses it, then <c>CLICOLOR_FORCE=1</c> forces it.
	/// </summary>
	public static bool? ResolveColorOverride()
	{
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_COLOR")))
		{
			return true;
		}

		if (IsAnsiOptOutEnvironment())
		{
			return false;
		}

		return IsCliColorForced() ? true : null;
	}

	/// <summary>
	/// True when the environment opts out of ANSI output: <c>NO_COLOR</c> with any non-blank value, or
	/// <c>TERM=dumb</c> unless <c>CLICOLOR_FORCE=1</c>. <c>TerminalAnsiCapability</c> decides how each gate
	/// weighs it.
	/// </summary>
	public static bool IsAnsiOptOutEnvironment()
	{
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR")))
		{
			return true;
		}

		return !IsCliColorForced()
			&& string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCliColorForced() =>
		string.Equals(Environment.GetEnvironmentVariable("CLICOLOR_FORCE"), "1", StringComparison.Ordinal);
}
