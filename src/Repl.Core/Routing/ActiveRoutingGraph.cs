namespace Repl;

internal readonly record struct ActiveRoutingGraph(
	RouteDefinition[] Routes,
	ContextDefinition[] Contexts,
	ReplRuntimeChannel Channel);
