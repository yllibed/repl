namespace Repl;

/// <summary>
/// Context metadata.
/// </summary>
public sealed record ReplDocContext(
	string Path,
	string? Description,
	bool IsDynamic,
	bool IsHidden);