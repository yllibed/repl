namespace Repl;

internal sealed record GlobalOptionDefinition(
	string Name,
	string CanonicalToken,
	IReadOnlyList<string> Aliases,
	string? DefaultValue);
