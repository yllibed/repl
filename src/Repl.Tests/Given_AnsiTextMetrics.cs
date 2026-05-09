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
	[Description("Visible length ignores ANSI OSC hyperlinks.")]
	public void When_TextContainsOscHyperlink_Then_VisibleLengthIgnoresControlBytes()
	{
		var link = "\u001b]8;;https://example.invalid\u0007link\u001b]8;;\u0007";

		AnsiTextMetrics.GetVisualLength(link).Should().Be(4);
	}
}
