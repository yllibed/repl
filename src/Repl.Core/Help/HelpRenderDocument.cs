namespace Repl;

internal sealed record HelpRenderDocument(
	string Scope,
	bool IsCommandHelp,
	IReadOnlyList<HelpRenderCommand> Commands,
	IReadOnlyList<HelpRenderEntry> Scopes,
	IReadOnlyList<HelpRenderEntry> GlobalOptions,
	IReadOnlyList<HelpRenderEntry> GlobalCommands);
