using System.Globalization;

namespace Repl;

internal sealed class ConsoleReplInteractionPresenter(
	InteractionOptions options,
	OutputOptions? outputOptions = null) : IReplInteractionPresenter
{
	private const string OscPrefix = "\x1b]9;4;";
	private const string Bell = "\x07";
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

			case ReplNoticeEvent notice:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await ReplSessionIO.Output.WriteLineAsync(Styled(notice.Text, _palette?.NoticeStyle)).ConfigureAwait(false);
				break;

			case ReplWarningEvent warning:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await ReplSessionIO.Output.WriteLineAsync(Styled($"Warning: {warning.Text}", _palette?.WarningStyle)).ConfigureAwait(false);
				break;

			case ReplProblemEvent problem:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				var header = string.IsNullOrWhiteSpace(problem.Code)
					? $"Problem: {problem.Summary}"
					: $"Problem [{problem.Code}]: {problem.Summary}";
				await ReplSessionIO.Output.WriteLineAsync(Styled(header, _palette?.ProblemStyle)).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(problem.Details))
				{
					await ReplSessionIO.Output.WriteLineAsync(Styled(problem.Details, _palette?.ProblemStyle)).ConfigureAwait(false);
				}

				break;

			case ReplPromptEvent prompt:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await ReplSessionIO.Output.WriteAsync($"{Styled(prompt.PromptText, _palette?.PromptStyle)}: ").ConfigureAwait(false);
				break;

			case ReplProgressEvent progress:
				await WriteProgressAsync(progress).ConfigureAwait(false);
				break;

			case ReplClearScreenEvent:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				if (_useAnsi)
				{
					await ReplSessionIO.Output.WriteAsync("\x1b[2J\x1b[H").ConfigureAwait(false);
				}

				break;
		}
	}

	private async ValueTask WriteProgressAsync(ReplProgressEvent progress)
	{
		if (progress.State == ReplProgressState.Clear)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
			await TryWriteAdvancedProgressAsync(progress).ConfigureAwait(false);
			return;
		}

		var percent = progress.ResolvePercent();
		var payload = FormatProgress(progress, percent);
		await TryWriteAdvancedProgressAsync(progress).ConfigureAwait(false);
		if (!_rewriteProgress)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
			await ReplSessionIO.Output.WriteLineAsync(Styled(payload, _palette?.ProgressStyle)).ConfigureAwait(false);
			return;
		}

		var styledPayload = Styled(payload, ResolveProgressStyle(progress.State));
		var paddingWidth = Math.Max(_lastProgressLength, payload.Length);
		var paddedPayload = _useAnsi
			? styledPayload + new string(' ', Math.Max(0, paddingWidth - payload.Length))
			: payload.PadRight(paddingWidth);
		await ReplSessionIO.Output.WriteAsync($"\r{paddedPayload}").ConfigureAwait(false);
		_lastProgressLength = payload.Length;
		_hasOpenProgressLine = true;
		if (progress.State != ReplProgressState.Indeterminate && percent >= 100d)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
		}
	}

	private string FormatProgress(ReplProgressEvent progress, double? percent)
	{
		if (progress.State == ReplProgressState.Indeterminate)
		{
			return string.IsNullOrWhiteSpace(progress.Details)
				? progress.Label
				: $"{progress.Label}: {progress.Details}";
		}

		var template = string.IsNullOrWhiteSpace(_options.ProgressTemplate)
			? "{label}: {percent:0}%"
			: _options.ProgressTemplate;
		var safeLabel = string.IsNullOrWhiteSpace(progress.Label)
			? _options.DefaultProgressLabel
			: progress.Label;
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

	private async ValueTask TryWriteAdvancedProgressAsync(ReplProgressEvent progress)
	{
		if (!ShouldEmitAdvancedProgress())
		{
			return;
		}

		var sequence = BuildAdvancedProgressSequence(progress);
		if (sequence is null)
		{
			return;
		}

		await ReplSessionIO.Output.WriteAsync(sequence).ConfigureAwait(false);
	}

	private bool ShouldEmitAdvancedProgress()
	{
		if (ReplSessionIO.IsProtocolPassthrough || !_useAnsi || !IsInteractiveTerminalSession())
		{
			return false;
		}

		return _options.AdvancedProgressMode switch
		{
			AdvancedProgressMode.Always => true,
			AdvancedProgressMode.Never => false,
			_ => true,
		};
	}

	private static bool IsInteractiveTerminalSession() =>
		(!Console.IsOutputRedirected || ReplSessionIO.IsSessionActive)
		&& !ReplSessionIO.IsProtocolPassthrough;

	private static string? BuildAdvancedProgressSequence(ReplProgressEvent progress)
	{
		var stateCode = progress.State switch
		{
			ReplProgressState.Normal => 1,
			ReplProgressState.Warning => 4,
			ReplProgressState.Error => 2,
			ReplProgressState.Indeterminate => 3,
			ReplProgressState.Clear => 0,
			_ => 1,
		};

		if (progress.State is ReplProgressState.Indeterminate or ReplProgressState.Clear)
		{
			return $"{OscPrefix}{stateCode};0{Bell}";
		}

		var percent = (int)Math.Clamp(
			Math.Round(progress.ResolvePercent() ?? 0d, MidpointRounding.AwayFromZero),
			0,
			100);
		return $"{OscPrefix}{stateCode};{percent}{Bell}";
	}

	private string? ResolveProgressStyle(ReplProgressState state) =>
		state switch
		{
			ReplProgressState.Warning => _palette?.WarningStyle,
			ReplProgressState.Error => _palette?.ProblemStyle,
			_ => _palette?.ProgressStyle,
		};

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
