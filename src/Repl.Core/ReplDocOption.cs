namespace Repl;

/// <summary>
/// Option metadata.
/// </summary>
public sealed record ReplDocOption(
	string Name,
	string Type,
	bool Required,
	string? Description);