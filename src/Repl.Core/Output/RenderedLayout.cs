namespace Repl;

/// <summary>
/// What a result-flow transformer rendered around a payload's data: the header its first
/// <see cref="HeaderLineCount"/> lines hold, the columns that header names, and the footer its last
/// <see cref="FooterLineCount"/> lines hold. The pager takes these lines as declared rather than inferring them
/// from the text's styling.
/// </summary>
internal sealed class RenderedLayout
{
	/// <param name="headerLineCount">How many of the payload's first lines are its header, separator included.</param>
	/// <param name="columns">The columns the header names, in order, as the transformer keys them.</param>
	/// <param name="footerLineCount">How many of the payload's last lines are its footer.</param>
	public RenderedLayout(int headerLineCount, IReadOnlyList<string> columns, int footerLineCount = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(headerLineCount);
		ArgumentNullException.ThrowIfNull(columns);
		ArgumentOutOfRangeException.ThrowIfNegative(footerLineCount);
		if (headerLineCount == 0 != (columns.Count == 0))
		{
			throw new ArgumentException("A header names at least one column, and only a header names columns.", nameof(columns));
		}

		HeaderLineCount = headerLineCount;
		Columns = columns;
		FooterLineCount = footerLineCount;
	}

	/// <summary>A payload rendered with neither a header nor a footer, such as a list or an object's fields.</summary>
	public static RenderedLayout None { get; } = new(headerLineCount: 0, columns: []);

	public int HeaderLineCount { get; }

	public IReadOnlyList<string> Columns { get; }

	public int FooterLineCount { get; }

	/// <summary>
	/// Whether this layout's header names the same columns as <paramref name="other"/>'s, compared ordinally: the
	/// pager drops a fetched page's header only when it repeats the columns the previous page showed.
	/// </summary>
	public bool NamesSameColumns(RenderedLayout other)
	{
		ArgumentNullException.ThrowIfNull(other);
		return HeaderLineCount > 0 && Columns.SequenceEqual(other.Columns, StringComparer.Ordinal);
	}

	/// <summary>This layout with a footer of <paramref name="footerLineCount"/> lines appended below the data.</summary>
	public RenderedLayout WithFooter(int footerLineCount) => new(HeaderLineCount, Columns, footerLineCount);
}
