namespace Repl;

/// <summary>
/// Application metadata.
/// </summary>
public sealed record ReplDocApp(
	string Name,
	string? Version,
	string? Description);