namespace Repl.Interaction;

/// <summary>
/// Requests a progress update display.
/// </summary>
public sealed record WriteProgressRequest(
	string Label,
	double? Percent,
	CancellationToken CancellationToken = default) : InteractionRequest<bool>("__progress__", Label);
