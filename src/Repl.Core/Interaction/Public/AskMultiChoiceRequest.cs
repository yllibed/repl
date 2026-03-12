namespace Repl.Interaction;

/// <summary>
/// Requests multi-selection from a list of choices.
/// </summary>
public sealed record AskMultiChoiceRequest(
	string Name,
	string Prompt,
	IReadOnlyList<string> Choices,
	IReadOnlyList<int>? DefaultIndices = null,
	AskMultiChoiceOptions? Options = null) : InteractionRequest<IReadOnlyList<int>>(Name, Prompt);
