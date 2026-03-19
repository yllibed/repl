namespace Repl.Documentation;

/// <summary>
/// Command metadata.
/// </summary>
public sealed record ReplDocCommand(
	string Path,
	string? Description,
	IReadOnlyList<string> Aliases,
	bool IsHidden,
	IReadOnlyList<ReplDocArgument> Arguments,
	IReadOnlyList<ReplDocOption> Options,
	string? Details = null,
	CommandAnnotations? Annotations = null,
	IReadOnlyDictionary<string, object>? Metadata = null,
	bool IsResource = false,
	bool IsPrompt = false);
