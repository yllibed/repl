namespace Repl;

internal sealed class PagerSession
{
	private readonly PagerHeader _header;
	private readonly int _maxBufferedLines;

	public PagerSession(string initialPayload, bool hasMorePayload, int maxBufferedLines)
	{
		_maxBufferedLines = Math.Max(1, maxBufferedLines);
		var parsed = PagerPayloadParser.Parse(initialPayload, header: null);
		_header = parsed.Header;
		Lines = [];
		AppendContent(parsed.ContentLines, hasMorePayload);
		PageSize = 1;
		NextWindow = 1;
	}

	public IReadOnlyList<string> HeaderLines => _header.Lines;

	public List<string> Lines { get; }

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
		var available = _maxBufferedLines - Lines.Count;
		if (available <= 0)
		{
			BufferLimitReached = true;
			HasMorePayload = false;
			return;
		}

		var take = Math.Min(available, contentLines.Count);
		for (var i = 0; i < take; i++)
		{
			Lines.Add(contentLines[i]);
		}

		BufferLimitReached = take < contentLines.Count;
		HasMorePayload = !BufferLimitReached && hasMorePayload;
	}
}
