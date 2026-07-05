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
		    || normalized.Contains("ghostty", StringComparison.Ordinal)
		    || normalized.Contains("conemu", StringComparison.Ordinal)
		    || normalized.Contains("windows terminal", StringComparison.Ordinal)
		    || normalized.Contains("vscode", StringComparison.Ordinal)
		    || normalized.Contains("alacritty", StringComparison.Ordinal)
		    || normalized.Contains("rxvt", StringComparison.Ordinal)
		    || normalized.Contains("konsole", StringComparison.Ordinal)
		    || normalized.Contains("gnome", StringComparison.Ordinal)
		    || normalized.Contains("linux", StringComparison.Ordinal))
		{
			var capabilities = TerminalCapabilities.IdentityReporting
			       | TerminalCapabilities.Ansi
			       | TerminalCapabilities.ResizeReporting
			       | TerminalCapabilities.VtInput;
			if (normalized.Contains("wezterm", StringComparison.Ordinal)
				|| normalized.Contains("iterm", StringComparison.Ordinal)
				|| normalized.Contains("ghostty", StringComparison.Ordinal)
				|| normalized.Contains("conemu", StringComparison.Ordinal)
				|| normalized.Contains("windows terminal", StringComparison.Ordinal))
			{
				capabilities |= TerminalCapabilities.ProgressReporting;
			}

			// ConEmu handles OSC 9;4 progress but not FinalTerm/VS Code prompt marks,
			// so it deliberately stays out of this list.
			if (normalized.Contains("wezterm", StringComparison.Ordinal)
				|| normalized.Contains("iterm", StringComparison.Ordinal)
				|| normalized.Contains("ghostty", StringComparison.Ordinal)
				|| normalized.Contains("windows terminal", StringComparison.Ordinal)
				|| normalized.Contains("vscode", StringComparison.Ordinal))
			{
				capabilities |= TerminalCapabilities.ShellIntegrationMarks;
			}

			return capabilities;
		}

		return TerminalCapabilities.IdentityReporting;
	}
}
