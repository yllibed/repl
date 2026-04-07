namespace Repl.Interaction;

/// <summary>
/// Requests a yes/no confirmation.
/// </summary>
public sealed record AskConfirmationRequest(
	string Name,
	string Prompt,
	bool DefaultValue = false,
	AskOptions? Options = null) : InteractionRequest<bool>(Name, Prompt);
