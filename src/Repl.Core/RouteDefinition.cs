namespace Repl;

internal sealed class RouteDefinition(
	RouteTemplate template,
	CommandBuilder command)
{
	public RouteTemplate Template { get; } = template;

	public CommandBuilder Command { get; } = command;
}
