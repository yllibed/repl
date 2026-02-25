namespace Repl;

/// <summary>
/// Command metadata.
/// </summary>
public sealed record ReplDocCommand(
	string Path,
	string? Description,
	IReadOnlyList<string> Aliases,
	bool IsHidden,
	IReadOnlyList<ReplDocArgument> Arguments,
	IReadOnlyList<ReplDocOption> Options);