namespace Repl;

internal sealed class LiteralRouteSegment(string rawText) : RouteSegment(rawText)
{
	public string Value { get; } = rawText;
}
