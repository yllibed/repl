namespace Repl.Tests.TerminalSupport;

/// <summary>
/// Canonical environment shapes for terminal-detection tests, shared so each test class
/// doesn't maintain its own copy of the variable list.
/// </summary>
internal static class TerminalTestEnvironments
{
	/// <summary>
	/// No terminal-identifying variables set: environment-based detection must see nothing.
	/// </summary>
	public static readonly (string Name, string? Value)[] Neutral =
	[
		("TMUX", null),
		("TERM", null),
		("WT_SESSION", null),
		("ConEmuANSI", null),
		("TERM_PROGRAM", null),
	];
}
