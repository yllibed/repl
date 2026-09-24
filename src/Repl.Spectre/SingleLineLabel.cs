using Spectre.Console.Rendering;

namespace Repl.Spectre;

/// <summary>
/// A bold table header label that always renders on one line, truncated when its column cannot fit it, the way the
/// default human transformer truncates. The pager is told a table's header is one line tall, so a header cell must
/// never wrap onto a second one.
/// </summary>
internal sealed class SingleLineLabel : Renderable
{
	private const string Ellipsis = "...";
	private static readonly Style Bold = new(decoration: Decoration.Bold);

	private readonly Segment _label;
	private readonly int _width;

	public SingleLineLabel(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		_label = new Segment(TextTableFormatter.ToSingleLine(text), Bold);
		_width = _label.CellCount();
	}

	// Unable to wrap, it has no narrower form than its whole width.
	protected override Measurement Measure(RenderOptions options, int maxWidth)
	{
		var width = Math.Min(_width, maxWidth);
		return new Measurement(width, width);
	}

	// Always one segment, even for a column with no room at all, so the header row keeps its one line.
	protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
	{
		if (_width <= maxWidth)
		{
			return [_label];
		}

		if (maxWidth <= Ellipsis.Length)
		{
			return [Segment.Truncate(_label, Math.Max(0, maxWidth)) ?? new Segment(string.Empty)];
		}

		return
		[
			Segment.Truncate(_label, maxWidth - Ellipsis.Length) ?? new Segment(string.Empty),
			new Segment(Ellipsis, Bold),
		];
	}
}
