namespace Repl.Terminal;

internal static class AnsiSequences
{
	public const string EnterAlternateScreen = "\u001b[?1049h";
	public const string LeaveAlternateScreen = "\u001b[?1049l";
	public const string HideCursor = "\u001b[?25l";
	public const string ShowCursor = "\u001b[?25h";
	public const string CursorHome = "\u001b[H";
	public const string ClearToEndOfScreen = "\u001b[J";
	public const string DisableLineWrap = "\u001b[?7l";
	public const string EnableLineWrap = "\u001b[?7h";

	public static string CursorUp(int rows) => $"\u001b[{rows}A";
}
