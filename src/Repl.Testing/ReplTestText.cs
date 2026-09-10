using System.Text;
using System.Text.RegularExpressions;

namespace Repl.Testing;

/// <summary>
/// Command-line and captured-text handling shared by the session handle and the signal harness. Both
/// take a command line as one string and both compare captured output, so the tokenizer and the
/// normalizer live here rather than once per entry point.
/// </summary>
internal static partial class ReplTestText
{
	internal static string[] Tokenize(string value)
	{
		var tokens = new List<string>();
		var current = new StringBuilder();
		var inQuotes = false;
		foreach (var ch in value)
		{
			if (ch == '"')
			{
				inQuotes = !inQuotes;
				continue;
			}

			if (!inQuotes && char.IsWhiteSpace(ch))
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}

				continue;
			}

			current.Append(ch);
		}

		if (current.Length > 0)
		{
			tokens.Add(current.ToString());
		}

		return [.. tokens];
	}

	internal static string NormalizeOutput(string output)
	{
		if (string.IsNullOrEmpty(output))
		{
			return output;
		}

		var normalized = output.Replace("\r", string.Empty, StringComparison.Ordinal);
		return BuildAnsiEscapeRegex().Replace(normalized, string.Empty);
	}

	[GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.None, matchTimeoutMilliseconds: 50)]
	private static partial Regex BuildAnsiEscapeRegex();
}
