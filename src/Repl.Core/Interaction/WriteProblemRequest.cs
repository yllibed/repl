namespace Repl.Interaction;

/// <summary>
/// Requests a user-facing problem summary.
/// </summary>
public sealed record WriteProblemRequest(
	string Summary,
	string? Details = null,
	string? Code = null,
	CancellationToken CancellationToken = default) : InteractionRequest<bool>("__problem__", Summary);
