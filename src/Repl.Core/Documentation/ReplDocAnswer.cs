namespace Repl.Documentation;

/// <summary>
/// Answer slot metadata for commands with interactive prompts.
/// </summary>
public sealed record ReplDocAnswer(
	string Name,
	string Type,
	string? Description);
