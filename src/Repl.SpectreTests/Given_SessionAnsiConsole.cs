using System.Text;
using Repl.Spectre;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SessionAnsiConsole
{
	static Given_SessionAnsiConsole() =>
		// cp437 (below) ships in System.Text.Encoding.CodePages, not in the default set.
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

	[TestMethod]
	[Description("Unicode-capable encodings roundtrip the rounded probe, so Spectre keeps its full rounded borders on UTF sinks — even when the local output is redirected, since the bytes stay valid UTF for any modern reader.")]
	public void When_EncodingCarriesUnicode_Then_RoundedBordersAreSupported()
	{
		SessionAnsiConsole.ResolveBoxDrawingSupport(Encoding.UTF8, isLocalRedirected: false).Should().Be(BoxDrawingSupport.Rounded);
		SessionAnsiConsole.ResolveBoxDrawingSupport(Encoding.UTF8, isLocalRedirected: true).Should().Be(BoxDrawingSupport.Rounded);
		SessionAnsiConsole.ResolveBoxDrawingSupport(Encoding.Unicode, isLocalRedirected: false).Should().Be(BoxDrawingSupport.Rounded);
	}

	[TestMethod]
	[Description("A non-redirected console on a legacy OEM codepage decodes its own bytes, and cp437 carries the square safe-border glyphs: Spectre's own non-Unicode fallback (square borders) renders correctly there, so no transliteration is needed.")]
	public void When_OemCodepageOnRealConsole_Then_SquareSafeBordersAreSupported()
	{
		var cp437 = Encoding.GetEncoding(437);

		SessionAnsiConsole.ResolveBoxDrawingSupport(cp437, isLocalRedirected: false).Should().Be(BoxDrawingSupport.Square);
	}

	[TestMethod]
	[Description("The Rider Run window case (issue #46, second field report): a REDIRECTED local console on a legacy codepage emits single-byte OEM codes that the reading process — decoding UTF-8 — renders as U+FFFD. Even though cp437 can encode the square glyphs, only ASCII survives an unknown reader charset.")]
	public void When_OemCodepageIsRedirected_Then_OnlyAsciiSurvives()
	{
		var cp437 = Encoding.GetEncoding(437);

		SessionAnsiConsole.ResolveBoxDrawingSupport(cp437, isLocalRedirected: true).Should().Be(BoxDrawingSupport.Ascii);
	}

	[TestMethod]
	[Description("Encodings whose fallback turns every box-drawing glyph into '?' (ASCII, Latin1) get the ASCII transliteration: Spectre's square safe border would hit the very same encoder fallback.")]
	public void When_EncodingCannotCarryAnyBoxGlyph_Then_AsciiTransliterationApplies()
	{
		SessionAnsiConsole.ResolveBoxDrawingSupport(Encoding.ASCII, isLocalRedirected: false).Should().Be(BoxDrawingSupport.Ascii);
		SessionAnsiConsole.ResolveBoxDrawingSupport(Encoding.Latin1, isLocalRedirected: false).Should().Be(BoxDrawingSupport.Ascii);
	}

	[TestMethod]
	[Description("A custom encoding that throws from the probe (hosted transports can declare arbitrary TextWriter.Encoding implementations) must conservatively mean 'ASCII only' instead of crashing every console creation of the session.")]
	public void When_EncodingThrowsFromTheProbe_Then_AsciiIsConservativelyChosen()
	{
		SessionAnsiConsole.ResolveBoxDrawingSupport(new ThrowingEncoding(), isLocalRedirected: false).Should().Be(BoxDrawingSupport.Ascii);
	}

	// Stands in for a hosted transport declaring a quirky TextWriter.Encoding.
	private sealed class ThrowingEncoding : Encoding
	{
		public override int GetByteCount(char[] chars, int index, int count) => throw new NotSupportedException();

		public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => throw new NotSupportedException();

		public override int GetCharCount(byte[] bytes, int index, int count) => throw new NotSupportedException();

		public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => throw new NotSupportedException();

		public override int GetMaxByteCount(int charCount) => throw new NotSupportedException();

		public override int GetMaxCharCount(int byteCount) => throw new NotSupportedException();
	}
}
