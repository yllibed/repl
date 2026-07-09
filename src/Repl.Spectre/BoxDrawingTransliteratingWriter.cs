using System.Text;

namespace Repl.Spectre;

/// <summary>
/// Wraps a writer whose sink cannot carry any box-drawing glyph and transliterates those
/// glyphs to ASCII ('-', '|', '+', '#') on the way through. Spectre's own non-Unicode
/// fallback still emits square box glyphs (they exist in OEM codepages), so an ASCII-limited
/// transport — or a redirected local console whose reader decodes another charset — would
/// otherwise ship '?' or mojibake for every border. ASCII bytes are identical in every
/// charset, so the transliterated output is legible no matter who reads the stream.
/// </summary>
internal sealed class BoxDrawingTransliteratingWriter(TextWriter inner) : TextWriter
{
	// Box Drawing block (U+2500–U+257F) plus Block Elements (U+2580–U+259F, progress
	// bars and shades) — the ranges Spectre draws chrome from. LastLineDrawingChar is
	// the boundary between the two blocks: at or below it maps to line ASCII (-, |, +),
	// above it maps to '#'.
	private const char FirstBoxChar = '─';
	private const char LastLineDrawingChar = '╿';
	private const char LastBoxChar = '▟';

	public override Encoding Encoding => inner.Encoding;

	public override void Write(char value) => inner.Write(Transliterate(value));

	public override void Write(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		// Vectorized scan: segments without box glyphs (the common case) pass through zero-copy.
		if (value.AsSpan().IndexOfAnyInRange(FirstBoxChar, LastBoxChar) < 0)
		{
			inner.Write(value);
			return;
		}

		WriteTransliterated(value.AsSpan());
	}

	public override void Write(char[] buffer, int index, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);

		var span = buffer.AsSpan(index, count);
		if (span.IndexOfAnyInRange(FirstBoxChar, LastBoxChar) < 0)
		{
			inner.Write(buffer, index, count);
			return;
		}

		WriteTransliterated(span);
	}

	public override void Write(ReadOnlySpan<char> buffer)
	{
		if (buffer.IndexOfAnyInRange(FirstBoxChar, LastBoxChar) < 0)
		{
			inner.Write(buffer);
			return;
		}

		WriteTransliterated(buffer);
	}

	public override void WriteLine() => inner.WriteLine();

	public override void WriteLine(string? value)
	{
		// Clean lines (the common case) go through as a single inner call.
		if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAnyInRange(FirstBoxChar, LastBoxChar) < 0)
		{
			inner.WriteLine(value);
			return;
		}

		WriteTransliterated(value.AsSpan());
		inner.WriteLine();
	}

	public override void WriteLine(ReadOnlySpan<char> buffer)
	{
		Write(buffer);
		inner.WriteLine();
	}

	public override void Flush() => inner.Flush();

	public override Task WriteAsync(char value) => inner.WriteAsync(Transliterate(value));

	public override Task WriteAsync(string? value) =>
		value is null ? Task.CompletedTask : inner.WriteAsync(TransliterateToString(value));

	public override Task WriteLineAsync() => inner.WriteLineAsync();

	public override Task WriteLineAsync(string? value) =>
		inner.WriteLineAsync(value is null ? null : TransliterateToString(value));

	public override Task FlushAsync() => inner.FlushAsync();

	/// <summary>
	/// Maps one char: straight horizontal segments to '-', straight vertical segments to '|',
	/// every other box-drawing glyph (corners, tees, crosses, arcs, diagonals) to '+', and
	/// block elements to '#'. Anything outside those ranges passes through untouched.
	/// </summary>
	internal static char Transliterate(char value) => value switch
	{
		// Horizontal runs: light/heavy/double/dashed lines and half-segments.
		'─' or '━' or '┄' or '┅' or '┈' or '┉'
			or '╌' or '╍' or '═' or '╴' or '╶'
			or '╸' or '╺' or '╼' or '╾' => '-',
		// Vertical runs: light/heavy/double/dashed lines and half-segments.
		'│' or '┃' or '┆' or '┇' or '┊' or '┋'
			or '╎' or '╏' or '║' or '╵' or '╷'
			or '╹' or '╻' or '╽' or '╿' => '|',
		>= FirstBoxChar and <= LastLineDrawingChar => '+',
		> LastLineDrawingChar and <= LastBoxChar => '#',
		_ => value,
	};

	private void WriteTransliterated(ReadOnlySpan<char> value)
	{
		// Chunked stack buffer: no allocation regardless of the rendered width.
		Span<char> buffer = stackalloc char[256];
		while (!value.IsEmpty)
		{
			var chunk = value[..Math.Min(value.Length, buffer.Length)];
			for (var i = 0; i < chunk.Length; i++)
			{
				buffer[i] = Transliterate(chunk[i]);
			}

			inner.Write(buffer[..chunk.Length]);
			value = value[chunk.Length..];
		}
	}

	private static string TransliterateToString(string value) =>
		value.AsSpan().IndexOfAnyInRange(FirstBoxChar, LastBoxChar) < 0
			? value
			: string.Create(value.Length, value, static (destination, source) =>
			{
				for (var i = 0; i < source.Length; i++)
				{
					destination[i] = Transliterate(source[i]);
				}
			});
}
