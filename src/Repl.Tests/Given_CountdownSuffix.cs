using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_CountdownSuffix
{
	[TestMethod]
	[Description("Countdown suffix must only contain characters that occupy exactly one console cell, so that \\b-based erasure works correctly.")]
	[DataRow(10, "Skip")]
	[DataRow(9, "Skip")]
	[DataRow(1, "Overwrite")]
	[DataRow(5, null)]
	public void When_FormattingCountdownSuffix_Then_AllCharsAreSingleCell(
		int remainingSeconds, string? defaultLabel)
	{
		var suffix = ConsoleInteractionChannel.FormatCountdownSuffix(remainingSeconds, defaultLabel);

		// Every character must be a printable ASCII character (U+0020..U+007E).
		// Multi-cell Unicode chars (like → U+2192) break \b-based erase because
		// \b moves back 1 cell but the char occupies 2 cells in many terminals.
		foreach (var ch in suffix)
		{
			ch.Should().BeInRange('\u0020', '\u007E',
				$"character U+{(int)ch:X4} in \"{suffix}\" may occupy >1 console cell");
		}
	}

	[TestMethod]
	[Description("Countdown suffix with a default label includes the label.")]
	public void When_DefaultLabelProvided_Then_SuffixContainsLabel()
	{
		var suffix = ConsoleInteractionChannel.FormatCountdownSuffix(10, "Skip");

		suffix.Should().Contain("10s");
		suffix.Should().Contain("Skip");
	}

	[TestMethod]
	[Description("Countdown suffix without default label shows only seconds.")]
	public void When_NoDefaultLabel_Then_SuffixShowsOnlySeconds()
	{
		var suffix = ConsoleInteractionChannel.FormatCountdownSuffix(5, defaultLabel: null);

		suffix.Should().Contain("5s");
		suffix.Should().NotContain("->");
	}
}
