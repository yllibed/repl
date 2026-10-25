namespace Repl;

internal sealed class PagerSession
{
	private readonly PagerHeader _header;
	private readonly bool _declaresLayout;
	private RenderedLayout? _previousLayout;
	private readonly int _maxBufferedLines;
	private readonly List<string> _lines = [];
	private readonly IReadOnlyList<string> _readOnlyLines;

	/// <param name="initialPayload">The first payload.</param>
	/// <param name="hasMorePayload">Whether another payload can be fetched.</param>
	/// <param name="maxBufferedLines">How many content lines the session buffers at most.</param>
	/// <param name="layout">
	/// The layout the payload's transformer declared, or <see langword="null"/> for a transformer that declares
	/// none, whose headers and footers are then detected from the text. Decided once, for every payload of the
	/// session: one transformer renders them all.
	/// </param>
	public PagerSession(
		string initialPayload,
		bool hasMorePayload,
		int maxBufferedLines,
		RenderedLayout? layout = null)
	{
		_maxBufferedLines = Math.Max(1, maxBufferedLines);
		_readOnlyLines = _lines.AsReadOnly();
		_declaresLayout = layout is not null;
		_previousLayout = layout;
		var parsed = layout is null
			? PagerPayloadParser.Parse(initialPayload, header: null)
			: PagerPayloadParser.ParseDeclared(initialPayload, header: null, previousLayout: null, layout);
		_header = parsed.Header;
		AppendContent(parsed.ContentLines, hasMorePayload);
		PageSize = 1;
		NextWindow = 1;
	}

	public IReadOnlyList<string> HeaderLines => _header.Lines;

	public IReadOnlyList<string> Lines => _readOnlyLines;

	public int PageSize { get; set; }

	public int NextWindow { get; set; }

	public int Index { get; set; }

	public bool HasMorePayload { get; set; }

	public bool BufferLimitReached { get; private set; }

	public bool SourceReturnedNoData { get; set; }

	/// <param name="payload">The fetched payload.</param>
	/// <param name="hasMorePayload">Whether another payload can be fetched.</param>
	/// <param name="containsPresentationChrome">
	/// Whether an undeclared payload can carry footer lines to strip; a declared layout names its footer itself.
	/// </param>
	/// <param name="layout">
	/// The layout the payload's transformer declared; ignored, like a missing one, when the session detects.
	/// </param>
	public void Append(
		string payload,
		bool hasMorePayload,
		bool containsPresentationChrome = true,
		RenderedLayout? layout = null)
	{
		ParsedPagerPayload parsed;
		if (_declaresLayout)
		{
			var declared = layout ?? RenderedLayout.None;
			parsed = PagerPayloadParser.ParseDeclared(payload, _header, _previousLayout, declared);
			_previousLayout = declared;
		}
		else
		{
			parsed = PagerPayloadParser.Parse(payload, _header, containsPresentationChrome);
		}

		AppendContent(parsed.ContentLines, hasMorePayload);
	}

	private void AppendContent(IReadOnlyList<string> contentLines, bool hasMorePayload)
	{
		var available = _maxBufferedLines - _lines.Count;
		if (available <= 0)
		{
			BufferLimitReached = true;
			HasMorePayload = false;
			return;
		}

		var take = Math.Min(available, contentLines.Count);
		for (var i = 0; i < take; i++)
		{
			_lines.Add(contentLines[i]);
		}

		BufferLimitReached = take < contentLines.Count;
		HasMorePayload = hasMorePayload && !BufferLimitReached;
	}
}
