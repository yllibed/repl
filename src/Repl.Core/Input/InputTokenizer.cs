using System.Text;

namespace Repl;

internal static class InputTokenizer
{
	internal sealed record Token(string Value, int Start, int Length, bool IsIncomplete);

	internal static List<Token> Tokenize(string text)
	{
		var tokens = new List<Token>();
		var index = 0;
		while (index < text.Length)
		{
			while (index < text.Length && char.IsWhiteSpace(text[index])) { index++; }
			if (index == text.Length) { break; }
			tokens.Add(ReadToken(text, ref index));
		}
		return tokens;
	}

	private static Token ReadToken(string text, ref int index, List<int>? contentPositions = null)
	{
		var start = index;
		var value = new StringBuilder();
		char? quote = null;
		while (index < text.Length)
		{
			var ch = text[index];
			if (quote is null && char.IsWhiteSpace(ch)) { break; }
			index++;
			if (quote is null && ch is '\'' or '"') { quote = ch; }
			else if (quote == ch) { quote = null; }
			else { value.Append(ch); contentPositions?.Add(index - 1); }
		}
		return new(value.ToString(), start, index - start, quote is not null);
	}

	internal static int MapTokenCursor(string original, string formatted, int cursor)
	{
		if (cursor <= 0) { return 0; }
		if (cursor >= original.Length) { return formatted.Length; }
		var originalPositions = new List<int>();
		var formattedPositions = new List<int>();
		var index = 0;
		ReadToken(original, ref index, originalPositions);
		index = 0;
		ReadToken(formatted, ref index, formattedPositions);
		var decodedOffset = originalPositions.Count(position => position < cursor);
		if (decodedOffset < formattedPositions.Count) { return formattedPositions[decodedOffset]; }
		return formattedPositions.Count != 0 ? formattedPositions[^1] + 1 : Math.Min(1, formatted.Length);
	}

	internal static string Format(IReadOnlyList<string> arguments) => string.Join(' ', arguments.Select(Quote));

	private static string Quote(string value)
	{
		if (value.Length != 0 && !value.Any(static ch => char.IsWhiteSpace(ch) || ch is '\'' or '"'))
		{
			return value;
		}
		// Adjacent quoted segments encode a literal double quote without introducing shell escaping.
		return "\"" + value.Replace("\"", "\"'\"'\"", StringComparison.Ordinal) + "\"";
	}
}
