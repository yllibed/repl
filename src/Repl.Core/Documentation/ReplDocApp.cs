namespace Repl.Documentation;

/// <summary>
/// Application metadata.
/// </summary>
public sealed record ReplDocApp(
	string Name,
	string? Version,
	string? Description);
