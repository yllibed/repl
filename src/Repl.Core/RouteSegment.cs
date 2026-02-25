namespace Repl;

internal abstract class RouteSegment
{
	protected RouteSegment(string rawText)
	{
		RawText = rawText;
	}

	public string RawText { get; }
}
