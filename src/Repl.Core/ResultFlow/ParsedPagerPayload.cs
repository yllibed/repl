namespace Repl;

internal sealed record ParsedPagerPayload(PagerHeader Header, IReadOnlyList<string> ContentLines)
{
	public int TotalLineCount => Header.Lines.Count + ContentLines.Count;
}
