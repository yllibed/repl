namespace Repl;

internal readonly record struct PrefixTemplate(
	RouteTemplate Template,
	bool IsHidden,
	IReadOnlyList<string> Aliases);
