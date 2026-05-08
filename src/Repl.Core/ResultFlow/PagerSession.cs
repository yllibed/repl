namespace Repl;

internal sealed class PagerSession
{
	private readonly PagerHeader _header;
	private readonly int _maxBufferedLines;
	private readonly List<string> _lines = [];
	private readonly IReadOnlyList<string> _readOnlyLines;

	public PagerSession(string initialPayload, bool hasMorePayload, int maxBufferedLines)
	{
		_maxBufferedLines = Math.Max(1, maxBufferedLines);
		_readOnlyLines = _lines.AsReadOnly();
		var parsed = PagerPayloadParser.Parse(initialPayload, header: null);
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

	public void Append(string payload, bool hasMorePayload, bool containsPresentationChrome = true)
	{
		var parsed = PagerPayloadParser.Parse(payload, _header, containsPresentationChrome);
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
		HasMorePayload = !BufferLimitReached && hasMorePayload;
	}
}
