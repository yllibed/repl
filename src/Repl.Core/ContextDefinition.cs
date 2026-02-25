namespace Repl;

internal sealed class ContextDefinition(
	RouteTemplate template,
	Delegate? validation,
	string? description)
{
	public RouteTemplate Template { get; } = template;

	public Delegate? Validation { get; } = validation;

	public string? Description { get; } = description;

	public Delegate? Banner { get; set; }
}
