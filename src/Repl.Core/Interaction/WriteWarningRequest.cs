namespace Repl.Interaction;

/// <summary>
/// Requests a user-facing warning.
/// </summary>
public sealed record WriteWarningRequest(
	string Text,
	CancellationToken CancellationToken = default) : InteractionRequest<bool>("__warning__", Text);
