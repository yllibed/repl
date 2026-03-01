namespace Repl;

/// <summary>
/// Option metadata.
/// </summary>
public sealed record ReplDocOption(
	string Name,
	string Type,
	bool Required,
	string? Description,
	IReadOnlyList<string> Aliases,
	IReadOnlyList<string> ReverseAliases,
	IReadOnlyList<ReplDocValueAlias> ValueAliases,
	IReadOnlyList<string> EnumValues,
	string? DefaultValue);
