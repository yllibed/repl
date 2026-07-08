using System.Text;
using Repl.Spectre;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SessionAnsiConsole
{
	[TestMethod]
	[Description("Unicode-capable encodings pass the box-drawing trial-encode, so Spectre keeps its rounded borders on UTF sinks.")]
	public void When_EncodingCarriesUnicode_Then_BoxDrawingIsRenderable()
	{
		SessionAnsiConsole.CanRenderBoxDrawing(Encoding.UTF8).Should().BeTrue();
		SessionAnsiConsole.CanRenderBoxDrawing(Encoding.Unicode).Should().BeTrue();
	}

	[TestMethod]
	[Description("Legacy encodings whose fallback turns box-drawing glyphs into '?' fail the trial-encode, so Spectre falls back to ASCII-safe borders instead of shipping mojibake.")]
	public void When_EncodingCannotCarryBoxDrawing_Then_FallbackIsDetected()
	{
		SessionAnsiConsole.CanRenderBoxDrawing(Encoding.ASCII).Should().BeFalse();
		SessionAnsiConsole.CanRenderBoxDrawing(Encoding.Latin1).Should().BeFalse();
	}

	[TestMethod]
	[Description("A custom encoding that throws from the probe (hosted transports can declare arbitrary TextWriter.Encoding implementations) must conservatively mean 'no box drawing' instead of crashing every console creation of the session.")]
	public void When_EncodingThrowsFromTheProbe_Then_BoxDrawingIsConservativelyDenied()
	{
		SessionAnsiConsole.CanRenderBoxDrawing(new ThrowingEncoding()).Should().BeFalse();
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
