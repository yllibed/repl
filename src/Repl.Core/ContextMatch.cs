namespace Repl;

internal sealed class ContextMatch(
	ContextDefinition context,
	IReadOnlyDictionary<string, string> routeValues)
{
	public ContextDefinition Context { get; } = context;

	public IReadOnlyDictionary<string, string> RouteValues { get; } = routeValues;
}
