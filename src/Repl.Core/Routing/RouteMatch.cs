namespace Repl;

internal sealed class RouteMatch(
	RouteDefinition route,
	IReadOnlyDictionary<string, string> values,
	IReadOnlyList<string> remainingTokens,
	int score)
{
	public RouteDefinition Route { get; } = route;

	public IReadOnlyDictionary<string, string> Values { get; } = values;

	public IReadOnlyList<string> RemainingTokens { get; } = remainingTokens;

	public int Score { get; } = score;
}
