namespace Repl.Tests.TerminalSupport;

/// <summary>
/// Test assertions over terminal shell-integration mark output.
/// </summary>
internal static class TerminalMarks
{
	/// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="text"/>.</summary>
	public static int Count(string text, string needle) =>
		text.AsSpan().Count(needle.AsSpan());
}
