namespace Repl.Interaction;

/// <summary>
/// Requests a status message display.
/// </summary>
public sealed record WriteStatusRequest(
	string Text,
	CancellationToken CancellationToken = default) : InteractionRequest<bool>("__status__", Text);
