using System.Globalization;

namespace Repl;

internal sealed class ConsoleReplInteractionPresenter(
	InteractionOptions options,
	OutputOptions? outputOptions = null) : IReplInteractionPresenter
{
	private readonly InteractionOptions _options = options;
	private readonly bool _rewriteProgress = !Console.IsOutputRedirected || ReplSessionIO.IsSessionActive;
	private readonly bool _useAnsi = outputOptions?.IsAnsiEnabled() ?? false;
	private readonly AnsiPalette? _palette = outputOptions is not null && outputOptions.IsAnsiEnabled()
		? outputOptions.ResolvePalette()
		: null;
	private int _lastProgressLength;
	private bool _hasOpenProgressLine;

	public async ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(evt);
		cancellationToken.ThrowIfCancellationRequested();
		_options.Observer?.OnInteractionEvent(evt);

		switch (evt)
		{
			case ReplStatusEvent status:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await ReplSessionIO.Output.WriteLineAsync(Styled(status.Text, _palette?.StatusStyle)).ConfigureAwait(false);
				break;

			case ReplPromptEvent prompt:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await ReplSessionIO.Output.WriteAsync($"{Styled(prompt.PromptText, _palette?.PromptStyle)}: ").ConfigureAwait(false);
				break;

			case ReplProgressEvent progress:
				await WriteProgressAsync(progress).ConfigureAwait(false);
				break;
		}
	}

	private async ValueTask WriteProgressAsync(ReplProgressEvent progress)
	{
		var percent = progress.ResolvePercent();
		var payload = FormatProgress(progress.Label, percent);
		if (!_rewriteProgress || percent is null)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
			await ReplSessionIO.Output.WriteLineAsync(Styled(payload, _palette?.ProgressStyle)).ConfigureAwait(false);
			return;
		}

		var styledPayload = Styled(payload, _palette?.ProgressStyle);
		var paddingWidth = Math.Max(_lastProgressLength, payload.Length);
		var paddedPayload = _useAnsi
			? styledPayload + new string(' ', Math.Max(0, paddingWidth - payload.Length))
			: payload.PadRight(paddingWidth);
		await ReplSessionIO.Output.WriteAsync($"\r{paddedPayload}").ConfigureAwait(false);
		_lastProgressLength = payload.Length;
		_hasOpenProgressLine = true;
		if (percent >= 100d)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
		}
	}

	private string FormatProgress(string label, double? percent)
	{
		var template = string.IsNullOrWhiteSpace(_options.ProgressTemplate)
			? "{label}: {percent:0}%"
			: _options.ProgressTemplate;
		var safeLabel = string.IsNullOrWhiteSpace(label)
			? _options.DefaultProgressLabel
			: label;
		var resolvedPercent = percent ?? 0d;
		var percentText = resolvedPercent.ToString("0.###", CultureInfo.InvariantCulture);
		var percentOneDecimalText = resolvedPercent.ToString("0.0", CultureInfo.InvariantCulture);
		var percentZeroDecimalText = resolvedPercent.ToString("0", CultureInfo.InvariantCulture);

		return template
			.Replace("{label}", safeLabel, StringComparison.Ordinal)
			.Replace("{percent:0.0}", percentOneDecimalText, StringComparison.Ordinal)
			.Replace("{percent:0}", percentZeroDecimalText, StringComparison.Ordinal)
			.Replace("{percent}", percentText, StringComparison.Ordinal);
	}

	private async ValueTask CloseProgressLineIfNeededAsync()
	{
		if (!_hasOpenProgressLine)
		{
			return;
		}

		await ReplSessionIO.Output.WriteLineAsync().ConfigureAwait(false);
		_hasOpenProgressLine = false;
		_lastProgressLength = 0;
	}

	private static string Styled(string text, string? style) =>
		string.IsNullOrEmpty(style) ? text : AnsiText.Apply(text, style);
}
