using System.Text;

namespace Repl;

internal static class ResponseFileTokenizer
{
	public static IReadOnlyList<string> Tokenize(string content)
	{
		ArgumentNullException.ThrowIfNull(content);

		var tokens = new List<string>();
		var current = new StringBuilder();
		var inSingleQuote = false;
		var inDoubleQuote = false;
		var escaping = false;

		for (var index = 0; index < content.Length; index++)
		{
			var ch = content[index];
			if (escaping)
			{
				current.Append(ch);
				escaping = false;
				continue;
			}

			if (ch == '\\')
			{
				escaping = true;
				continue;
			}

			if (!inSingleQuote && ch == '"')
			{
				inDoubleQuote = !inDoubleQuote;
				continue;
			}

			if (!inDoubleQuote && ch == '\'')
			{
				inSingleQuote = !inSingleQuote;
				continue;
			}

			if (!inSingleQuote && !inDoubleQuote && ch == '#')
			{
				FinalizeToken(tokens, current);
				while (index + 1 < content.Length && content[index + 1] is not '\r' and not '\n')
				{
					index++;
				}

				continue;
			}

			if (!inSingleQuote && !inDoubleQuote && char.IsWhiteSpace(ch))
			{
				FinalizeToken(tokens, current);
				continue;
			}

			current.Append(ch);
		}

		FinalizeToken(tokens, current);
		return tokens;
	}

	private static void FinalizeToken(List<string> tokens, StringBuilder current)
	{
		if (current.Length == 0)
		{
			return;
		}

		tokens.Add(current.ToString());
		current.Clear();
	}
}
