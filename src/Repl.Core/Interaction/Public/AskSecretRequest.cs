namespace Repl.Interaction;

/// <summary>
/// Requests masked secret input (password, token).
/// </summary>
public sealed record AskSecretRequest(
	string Name,
	string Prompt,
	AskSecretOptions? Options = null) : InteractionRequest<string>(Name, Prompt);
