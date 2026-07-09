using Repl.Spectre;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_BoxDrawingTransliteratingWriter
{
	[TestMethod]
	[Description("Box-drawing glyphs map to their ASCII skeleton: straight horizontals to '-', straight verticals to '|', corners/tees/crosses/arcs to '+', block elements (progress bars) to '#'. ASCII output is identical in every charset, so it is legible no matter who decodes the stream.")]
	public void When_WritingBoxDrawing_Then_GlyphsAreTransliteratedToAscii()
	{
		using var inner = new StringWriter();
		using var sut = new BoxDrawingTransliteratingWriter(inner);

		sut.Write("╭─┬╮ │ ├┼┤ ╰┴╯ ┌═┐ ━ ║ █▓");

		inner.ToString().Should().Be("+-++ | +++ +++ +-+ - | ##");
	}

	[TestMethod]
	[Description("Text without box glyphs passes through untouched (and takes the zero-copy fast path): only terminal chrome is rewritten, never payload data.")]
	public void When_WritingPlainText_Then_ContentPassesThroughUnchanged()
	{
		using var inner = new StringWriter();
		using var sut = new BoxDrawingTransliteratingWriter(inner);

		sut.Write("bib overalls x42 — denim: ga bu zo meu");
		sut.Write('!');

		inner.ToString().Should().Be("bib overalls x42 — denim: ga bu zo meu!");
	}

	[TestMethod]
	[Description("The char-array overload transliterates too — Spectre's writer plumbing goes through several TextWriter entry points and none may leak a raw glyph.")]
	public void When_WritingThroughCharArrayOverload_Then_GlyphsAreTransliterated()
	{
		using var inner = new StringWriter();
		using var sut = new BoxDrawingTransliteratingWriter(inner);
		var buffer = "a│b".ToCharArray();

		sut.Write(buffer, 0, buffer.Length);

		inner.ToString().Should().Be("a|b");
	}
}
