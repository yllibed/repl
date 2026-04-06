namespace Repl;

internal static class TerminalCapabilitiesClassifier
{
	public static TerminalCapabilities InferFromIdentity(string? terminalIdentity)
	{
		if (string.IsNullOrWhiteSpace(terminalIdentity))
		{
			return TerminalCapabilities.None;
		}

		var normalized = terminalIdentity.Trim().ToLowerInvariant();
		if (normalized.Contains("dumb", StringComparison.Ordinal))
		{
			return TerminalCapabilities.IdentityReporting;
		}

		if (normalized.Contains("xterm", StringComparison.Ordinal)
		    || normalized.Contains("vt", StringComparison.Ordinal)
		    || normalized.Contains("ansi", StringComparison.Ordinal)
		    || normalized.Contains("screen", StringComparison.Ordinal)
		    || normalized.Contains("tmux", StringComparison.Ordinal)
		    || normalized.Contains("wezterm", StringComparison.Ordinal)
		    || normalized.Contains("iterm", StringComparison.Ordinal)
		    || normalized.Contains("alacritty", StringComparison.Ordinal)
		    || normalized.Contains("rxvt", StringComparison.Ordinal)
		    || normalized.Contains("konsole", StringComparison.Ordinal)
		    || normalized.Contains("gnome", StringComparison.Ordinal)
		    || normalized.Contains("linux", StringComparison.Ordinal))
		{
			return TerminalCapabilities.IdentityReporting
			       | TerminalCapabilities.Ansi
			       | TerminalCapabilities.ResizeReporting
			       | TerminalCapabilities.VtInput;
		}

		return TerminalCapabilities.IdentityReporting;
	}
}
