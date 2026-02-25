namespace Repl;

internal sealed record HelpCommandModel(
	string Name,
	string Description,
	string Usage,
	IReadOnlyList<string> Aliases);
