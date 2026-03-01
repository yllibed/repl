namespace Repl.Documentation;

/// <summary>
/// Argument metadata.
/// </summary>
public sealed record ReplDocArgument(
	string Name,
	string Type,
	bool Required,
	string? Description);
