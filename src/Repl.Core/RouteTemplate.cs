namespace Repl;

internal sealed class RouteTemplate(
	string template,
	IReadOnlyList<RouteSegment> segments)
{
	public string Template { get; } = template;

	public IReadOnlyList<RouteSegment> Segments { get; } = segments;
}
