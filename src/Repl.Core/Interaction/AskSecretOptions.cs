namespace Repl.Interaction;

/// <summary>
/// Options for <see cref="IReplInteractionChannel.AskSecretAsync"/>.
/// </summary>
/// <param name="Timeout">
/// Optional timeout for the prompt. When the timeout elapses, the prompt gets no answer: an empty string
/// when <paramref name="AllowEmpty"/> is set, otherwise it asks again. A local console shows the countdown
/// inline.
/// </param>
/// <param name="Mask">
/// Character used to echo each typed character. Use <c>'*'</c> for asterisks
/// or <c>null</c> for invisible (no echo).
/// </param>
/// <param name="AllowEmpty">
/// When <c>false</c>, the prompt loops until a non-empty value is entered.
/// </param>
/// <param name="CancellationToken">
/// Explicit cancellation token. When <c>default</c>, the channel uses the
/// ambient per-command token set by the framework before each command dispatch.
/// </param>
public record AskSecretOptions(
	TimeSpan? Timeout = null,
	char? Mask = '*',
	bool AllowEmpty = false,
	CancellationToken CancellationToken = default);
