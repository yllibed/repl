namespace Repl.Documentation;

/// <summary>
/// Resource metadata for commands marked as data sources.
/// </summary>
public sealed record ReplDocResource(
	string Path,
	string? Description,
	string? Details,
	IReadOnlyList<ReplDocArgument> Arguments,
	IReadOnlyList<ReplDocOption> Options);
