namespace Repl;

internal sealed record HelpDocumentModel(
	string Scope,
	IReadOnlyList<HelpCommandModel> Commands,
	DateTimeOffset GeneratedAtUtc);
