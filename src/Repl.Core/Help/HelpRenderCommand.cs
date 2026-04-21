namespace Repl;

internal sealed record HelpRenderCommand(
	string Name,
	string Description,
	string Usage,
	IReadOnlyList<string> Aliases,
	IReadOnlyList<HelpRenderEntry> Arguments,
	IReadOnlyList<HelpRenderEntry> Options,
	IReadOnlyList<HelpRenderEntry> Answers);
