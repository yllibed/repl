namespace Repl;

internal static class AnsiText
{
	public const string Reset = "\u001b[0m";

	public static string Apply(string value, string style)
	{
		if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(style))
		{
			return value;
		}

		return string.Concat(style, value, Reset);
	}
}
