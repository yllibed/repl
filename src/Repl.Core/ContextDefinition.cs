namespace Repl;

internal sealed class ContextDefinition(
	RouteTemplate template,
	Delegate? validation,
	string? description,
	int moduleId)
{
	public RouteTemplate Template { get; } = template;

	public Delegate? Validation { get; } = validation;

	public string? Description { get; } = description;

	public Delegate? Banner { get; set; }

	public int ModuleId { get; } = moduleId;
}
