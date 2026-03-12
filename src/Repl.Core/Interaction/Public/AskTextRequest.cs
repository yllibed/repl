namespace Repl.Interaction;

/// <summary>
/// Requests free-form text input.
/// </summary>
public sealed record AskTextRequest(
	string Name,
	string Prompt,
	string? DefaultValue = null,
	AskOptions? Options = null) : InteractionRequest<string>(Name, Prompt);
