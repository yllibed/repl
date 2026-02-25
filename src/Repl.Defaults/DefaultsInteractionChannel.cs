namespace Repl;

internal sealed class DefaultsInteractionChannel : IReplInteractionChannel, ICommandTokenReceiver
{
	private readonly ConsoleInteractionChannel _inner;

	public DefaultsInteractionChannel(
		InteractionOptions options,
		OutputOptions? outputOptions = null,
		TimeProvider? timeProvider = null)
	{
		_inner = new ConsoleInteractionChannel(options, outputOptions, timeProvider: timeProvider);
	}

	void ICommandTokenReceiver.SetCommandToken(CancellationToken ct) =>
		((ICommandTokenReceiver)_inner).SetCommandToken(ct);

	public ValueTask WriteProgressAsync(string label, double? percent, CancellationToken cancellationToken) =>
		_inner.WriteProgressAsync(label, percent, cancellationToken);

	public ValueTask WriteStatusAsync(string text, CancellationToken cancellationToken) =>
		_inner.WriteStatusAsync(text, cancellationToken);

	public ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null) =>
		_inner.AskChoiceAsync(name, prompt, choices, defaultIndex, options);

	public ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool defaultValue = false,
		AskOptions? options = null) =>
		_inner.AskConfirmationAsync(name, prompt, defaultValue, options);

	public ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null) =>
		_inner.AskTextAsync(name, prompt, defaultValue, options);
}
