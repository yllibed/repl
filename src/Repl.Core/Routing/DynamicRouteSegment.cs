namespace Repl;

internal sealed class DynamicRouteSegment(
	string rawText,
	string name,
	RouteConstraintKind constraintKind,
	string? customConstraintName = null,
	bool isOptional = false) : RouteSegment(rawText)
{
	public string Name { get; } = name;

	public RouteConstraintKind ConstraintKind { get; } = constraintKind;

	public string? CustomConstraintName { get; } = customConstraintName;

	public bool IsOptional { get; } = isOptional;

	public int SpecificityScore { get; } = constraintKind == RouteConstraintKind.Custom
		? 3
		: (int)constraintKind;
}
