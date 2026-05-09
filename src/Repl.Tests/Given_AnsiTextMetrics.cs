using Repl.Terminal;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AnsiTextMetrics
{
	[TestMethod]
	[Description("Visible length ignores ANSI CSI styling bytes.")]
	public void When_TextContainsCsiSequence_Then_VisibleLengthIgnoresControlBytes()
	{
		AnsiTextMetrics.GetVisualLength("\u001b[1mhello\u001b[0m").Should().Be(5);
	}

	[TestMethod]
	[Description("Visible length can be computed from a caller-owned text slice without materializing a string.")]
	public void When_TextIsProvidedAsSpan_Then_VisibleLengthIsComputedFromSlice()
	{
		var text = "xx\u001b[1mhello\u001b[0myy";

		AnsiTextMetrics.GetVisualLength(text.AsSpan(2, text.Length - 4)).Should().Be(5);
	}

	[TestMethod]
	[Description("Visible length ignores ANSI OSC hyperlinks.")]
	public void When_TextContainsOscHyperlink_Then_VisibleLengthIgnoresControlBytes()
	{
		var link = "\u001b]8;;https://example.invalid\u0007link\u001b]8;;\u0007";

		AnsiTextMetrics.GetVisualLength(link).Should().Be(4);
	}

	[TestMethod]
	[Description("Visible length ignores OSC sequences terminated by ST.")]
	public void When_TextContainsOscSequenceTerminatedByStringTerminator_Then_VisibleLengthIgnoresControlBytes()
	{
		var link = "\u001b]8;;https://example.invalid\u001b\\link\u001b]8;;\u001b\\";

		AnsiTextMetrics.GetVisualLength(link).Should().Be(4);
	}

	[TestMethod]
	[Description("ANSI stripping can consume text slices without forcing the caller to allocate first.")]
	public void When_StrippingAnsiFromSpan_Then_ControlSequencesAreRemoved()
	{
		var text = "xx\u001b[1m#\u001b[0m  \u001b[1mAt\u001b[0myy";

		AnsiTextMetrics.StripControlSequences(text.AsSpan(2, text.Length - 4)).Should().Be("#  At");
	}
}
