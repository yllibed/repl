namespace Repl.Interaction;

/// <summary>
/// Requests a single choice from a list of options.
/// </summary>
public sealed record AskChoiceRequest(
	string Name,
	string Prompt,
	IReadOnlyList<string> Choices,
	int? DefaultIndex = null,
	AskOptions? Options = null) : InteractionRequest<int>(Name, Prompt);
