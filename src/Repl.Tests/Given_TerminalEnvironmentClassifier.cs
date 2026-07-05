using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TerminalEnvironmentClassifier
{
	[TestMethod]
	[Description("Multiplexer detection recognizes tmux via the TMUX variable so terminal-specific sequences stay off by default under multiplexers.")]
	public void When_TmuxEnvironmentIsActive_Then_MultiplexerSessionIsDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", "/tmp/tmux-1000/default,42,0"),
			("TERM", null));

		TerminalEnvironmentClassifier.IsTerminalMultiplexerSession().Should().BeTrue();
	}

	[TestMethod]
	[Description("Multiplexer detection recognizes GNU screen via the TERM prefix even without a TMUX variable.")]
	public void When_TermReportsScreen_Then_MultiplexerSessionIsDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", "screen-256color"));

		TerminalEnvironmentClassifier.IsTerminalMultiplexerSession().Should().BeTrue();
	}

	[TestMethod]
	[Description("Windows Terminal (WT_SESSION) is classified as an advanced-progress terminal, preserving the pre-refactor behavior of the progress presenter.")]
	public void When_WindowsTerminalSessionIsActive_Then_AdvancedProgressTerminalIsDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));

		TerminalEnvironmentClassifier.IsKnownAdvancedProgressTerminal().Should().BeTrue();
	}

	[TestMethod]
	[Description("ConEmu (ConEmuANSI=ON) is classified as an advanced-progress terminal, preserving the pre-refactor behavior of the progress presenter.")]
	public void When_ConEmuAnsiIsOn_Then_AdvancedProgressTerminalIsDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", null),
			("ConEmuANSI", "ON"),
			("TERM_PROGRAM", null));

		TerminalEnvironmentClassifier.IsKnownAdvancedProgressTerminal().Should().BeTrue();
	}

	[TestMethod]
	[Description("A multiplexer session suppresses advanced-progress classification even when the outer terminal is a known one.")]
	public void When_TmuxWrapsWindowsTerminal_Then_AdvancedProgressTerminalIsNotDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", "/tmp/tmux-1000/default,42,0"),
			("TERM", "tmux-256color"),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));

		TerminalEnvironmentClassifier.IsKnownAdvancedProgressTerminal().Should().BeFalse();
	}

	[TestMethod]
	[Description("TERM_PROGRAM=vscode identifies the VS Code integrated terminal and classifies it as shell-integration capable.")]
	public void When_VsCodeTermProgramIsSet_Then_ShellIntegrationTerminalIsDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", "vscode"));

		TerminalEnvironmentClassifier.IsVsCodeTerminal().Should().BeTrue();
		TerminalEnvironmentClassifier.IsKnownShellIntegrationTerminal().Should().BeTrue();
	}

	[TestMethod]
	[Description("Multiplexers suppress shell-integration classification in Auto mode: marks positioning is unreliable through tmux panes.")]
	public void When_TmuxIsActive_Then_ShellIntegrationTerminalIsNotDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", "/tmp/tmux-1000/default,42,0"),
			("TERM", "tmux-256color"),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", "vscode"));

		TerminalEnvironmentClassifier.IsKnownShellIntegrationTerminal().Should().BeFalse();
	}

	[TestMethod]
	[Description("ConEmu supports advanced progress but not OSC 133 marks, so ConEmuANSI alone must not classify the terminal as shell-integration capable.")]
	public void When_ConEmuOnly_Then_ShellIntegrationTerminalIsNotDetected()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", null),
			("ConEmuANSI", "ON"),
			("TERM_PROGRAM", null));

		TerminalEnvironmentClassifier.IsKnownShellIntegrationTerminal().Should().BeFalse();
	}
}
