namespace Repl.Protocol;

/// <summary>
/// Machine-readable help document.
/// </summary>
/// <param name="Scope">Current help scope.</param>
/// <param name="Commands">Discoverable commands in scope.</param>
/// <param name="GeneratedAtUtc">Generation timestamp.</param>
public sealed record HelpDocument(
	string Scope,
	IReadOnlyList<HelpCommand> Commands,
	DateTimeOffset GeneratedAtUtc);