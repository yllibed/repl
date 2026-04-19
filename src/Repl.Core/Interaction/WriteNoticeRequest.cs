namespace Repl.Interaction;

/// <summary>
/// Requests an informational user-facing notice.
/// </summary>
public sealed record WriteNoticeRequest(
	string Text,
	CancellationToken CancellationToken = default) : InteractionRequest<bool>("__notice__", Text);
