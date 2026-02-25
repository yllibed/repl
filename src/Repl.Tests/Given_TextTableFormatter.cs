using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_TextTableFormatter
{
	[TestMethod]
	[Description("Regression guard: verifies formatting rows with header separator so that aligned columns are rendered.")]
	public void When_FormattingRowsWithHeaderSeparator_Then_AlignedTableIsRendered()
	{
		var rows = new[]
		{
			new[] { "Name", "Email" },
			new[] { "Alice Martin", "alice@example.com" },
			new[] { "Bob", "bob@example.com" },
		};

		var text = TextTableFormatter.FormatRows(rows, renderWidth: 120, includeHeaderSeparator: true, TextTableStyle.None);

		var expected = string.Join(
			Environment.NewLine,
			"Name          Email",
			"------------  -----------------",
			"Alice Martin  alice@example.com",
			"Bob           bob@example.com");
		text.Should().Be(expected);
	}

	[TestMethod]
	[Description("Regression guard: verifies formatting rows with narrow width so that long values are truncated and each line fits.")]
	public void When_FormattingRowsWithNarrowWidth_Then_ValuesAreTruncatedToFit()
	{
		var rows = new[]
		{
			new[] { "Command", "Description" },
			new[] { "complete --target <name> [--input <text>] <path>", "Resolve completions." },
		};

		var text = TextTableFormatter.FormatRows(rows, renderWidth: 40, includeHeaderSeparator: false, TextTableStyle.None);

		text.Split(Environment.NewLine, StringSplitOptions.None)
			.Should().OnlyContain(line => line.Length <= 40);
		text.Should().Contain("...");
	}
}
