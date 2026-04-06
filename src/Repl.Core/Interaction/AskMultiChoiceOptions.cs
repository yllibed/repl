namespace Repl.Interaction;

/// <summary>
/// Options for <see cref="IReplInteractionChannel.AskMultiChoiceAsync"/>.
/// </summary>
/// <param name="Timeout">
/// Optional timeout for the prompt. When the timeout elapses, the default
/// selections are returned.
/// </param>
/// <param name="MinSelections">
/// Minimum number of selections required. <c>null</c> means no minimum.
/// </param>
/// <param name="MaxSelections">
/// Maximum number of selections allowed. <c>null</c> means no maximum.
/// </param>
/// <param name="CancellationToken">
/// Explicit cancellation token. When <c>default</c>, the channel uses the
/// ambient per-command token set by the framework before each command dispatch.
/// </param>
public record AskMultiChoiceOptions(
	TimeSpan? Timeout = null,
	int? MinSelections = null,
	int? MaxSelections = null,
	CancellationToken CancellationToken = default);
