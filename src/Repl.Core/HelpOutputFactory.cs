namespace Repl;

internal delegate object HelpOutputFactory(
	IReadOnlyList<RouteDefinition> routes,
	IReadOnlyList<ContextDefinition> contexts,
	IReadOnlyList<string> scopeTokens,
	ParsingOptions parsingOptions,
	AmbientCommandOptions ambientOptions);
