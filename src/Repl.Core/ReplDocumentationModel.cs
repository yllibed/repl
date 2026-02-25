namespace Repl;

/// <summary>
/// Structured documentation payload for the command graph.
/// </summary>
public sealed record ReplDocumentationModel(
	ReplDocApp App,
	IReadOnlyList<ReplDocContext> Contexts,
	IReadOnlyList<ReplDocCommand> Commands);