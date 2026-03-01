namespace Repl;

internal sealed class RouteDefinition(
	RouteTemplate template,
	CommandBuilder command,
	int moduleId,
	OptionSchema optionSchema)
{
	public RouteTemplate Template { get; } = template;

	public CommandBuilder Command { get; } = command;

	public int ModuleId { get; } = moduleId;

	public OptionSchema OptionSchema { get; } = optionSchema;
}
