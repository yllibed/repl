using System.Globalization;

namespace Repl;

internal sealed class ConsoleReplInteractionPresenter(
	InteractionOptions options,
	OutputOptions? outputOptions = null) : IReplInteractionPresenter
{
	private const string OscPrefix = "\x1b]9;4;";
	private const string Bell = "\x07";
	private readonly InteractionOptions _options = options;
	private readonly OutputOptions? _outputOptions = outputOptions;
	private readonly bool _useAnsi = outputOptions?.IsAnsiEnabled() ?? false;
	private readonly AnsiPalette? _palette = outputOptions is not null && outputOptions.IsAnsiEnabled()
		? outputOptions.ResolvePalette()
		: null;
	private int _lastProgressLength;

	// The writer the open progress line was written to: it is closed there, even when the next piece of
	// feedback goes elsewhere.
	private TextWriter? _openProgressLineWriter;

	public async ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(evt);
		cancellationToken.ThrowIfCancellationRequested();
		_options.Observer?.OnInteractionEvent(evt);
		var target = InteractionFeedbackRouting.Resolve(_outputOptions);
		var output = target.Writer;
		var palette = target.CanStyle(_useAnsi) ? _palette : null;

		switch (evt)
		{
			case ReplStatusEvent status:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled(status.Text, palette?.StatusStyle)).ConfigureAwait(false);
				break;

			case ReplNoticeEvent notice:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled(notice.Text, palette?.NoticeStyle)).ConfigureAwait(false);
				break;

			case ReplWarningEvent warning:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled($"Warning: {warning.Text}", palette?.WarningStyle)).ConfigureAwait(false);
				break;

			case ReplProblemEvent problem:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				var header = string.IsNullOrWhiteSpace(problem.Code)
					? $"Problem: {problem.Summary}"
					: $"Problem [{problem.Code}]: {problem.Summary}";
				await output.WriteLineAsync(Styled(header, palette?.ProblemStyle)).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(problem.Details))
				{
					await output.WriteLineAsync(Styled(problem.Details, palette?.ProblemStyle)).ConfigureAwait(false);
				}

				break;

			case ReplPromptEvent prompt:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				await output.WriteAsync($"{Styled(prompt.PromptText, palette?.PromptStyle)}: ").ConfigureAwait(false);
				break;

			case ReplProgressEvent progress:
				await WriteProgressAsync(progress, target, palette).ConfigureAwait(false);
				break;

			case ReplClearScreenEvent:
				await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
				if (palette is not null)
				{
					await output.WriteAsync("\x1b[2J\x1b[H").ConfigureAwait(false);
				}

				break;
		}
	}

	private async ValueTask WriteProgressAsync(ReplProgressEvent progress, FeedbackTarget target, AnsiPalette? palette)
	{
		var output = target.Writer;
		if (progress.State == ReplProgressState.Clear)
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
			await TryWriteAdvancedProgressAsync(progress, target).ConfigureAwait(false);
			return;
		}

		var percent = progress.ResolvePercent();
		var payload = FormatProgress(progress, percent);
		await TryWriteAdvancedProgressAsync(progress, target).ConfigureAwait(false);
		if (!target.IsTerminal || (progress.State == ReplProgressState.Normal && percent is null))
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
			await output.WriteLineAsync(Styled(payload, ResolveProgressStyle(progress.State, palette))).ConfigureAwait(false);
			return;
		}

		// Closed before padding: the width to overwrite is that of the line on this writer, if any.
		if (_openProgressLineWriter is { } openWriter && !ReferenceEquals(openWriter, output))
		{
			await CloseProgressLineIfNeededAsync().ConfigureAwait(false);
		}

		var styledPayload = Styled(payload, ResolveProgressStyle(progress.State, palette));
		var paddingWidth = Math.Max(_lastProgressLength, payload.Length);
		var paddedPayload = palette is not null
			? styledPayload + new string(' ', Math.Max(0, paddingWidth - payload.Length))
			: payload.PadRight(paddingWidth);
		await output.WriteAsync($"\r{paddedPayload}").ConfigureAwait(false);
		_lastProgressLength = payload.Length;
		_openProgressLineWriter = output;
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

		var formatted = template
			.Replace("{label}", safeLabel, StringComparison.Ordinal)
			.Replace("{percent:0.0}", percentOneDecimalText, StringComparison.Ordinal)
			.Replace("{percent:0}", percentZeroDecimalText, StringComparison.Ordinal)
			.Replace("{percent}", percentText, StringComparison.Ordinal);

		if (progress.State is ReplProgressState.Warning or ReplProgressState.Error
			&& !string.IsNullOrWhiteSpace(progress.Details))
		{
			return $"{formatted}: {progress.Details}";
		}

		return formatted;
	}

	private async ValueTask TryWriteAdvancedProgressAsync(ReplProgressEvent progress, FeedbackTarget target)
	{
		if (!ShouldEmitAdvancedProgress(target))
		{
			return;
		}

		var sequence = BuildAdvancedProgressSequence(progress);
		if (sequence is null)
		{
			return;
		}

		await target.Writer.WriteAsync(sequence).ConfigureAwait(false);
	}

	private bool ShouldEmitAdvancedProgress(FeedbackTarget target)
	{
		// Evaluated per event (not frozen at construction) through the shared ANSI gate,
		// so a hosted client advertising ANSI via capability flags alone — or mid-session —
		// gets the same treatment as shell-integration marks.
		var ansiCapable = _outputOptions is { } outputOptions
			&& TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(outputOptions);
		if (ReplSessionIO.IsProtocolPassthrough || !ansiCapable || !target.IsTerminal)
		{
			return false;
		}

		return _options.AdvancedProgressMode switch
		{
			AdvancedProgressMode.Always => true,
			AdvancedProgressMode.Never => false,
			_ => SessionAdvertisesAdvancedProgress() || TerminalEnvironmentClassifier.IsKnownAdvancedProgressTerminal(),
		};
	}

	private static bool SessionAdvertisesAdvancedProgress() =>
		ReplSessionIO.IsSessionActive
		&& ReplSessionIO.TerminalCapabilities.HasFlag(TerminalCapabilities.ProgressReporting);

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

	private static string? ResolveProgressStyle(ReplProgressState state, AnsiPalette? palette) =>
		state switch
		{
			ReplProgressState.Warning => palette?.WarningStyle,
			ReplProgressState.Error => palette?.ProblemStyle,
			_ => palette?.ProgressStyle,
		};

	private async ValueTask CloseProgressLineIfNeededAsync()
	{
		if (_openProgressLineWriter is not { } writer)
		{
			return;
		}

		_openProgressLineWriter = null;
		_lastProgressLength = 0;

		// This presenter is shared by every session of the app: a line another session left open is
		// forgotten, never closed from here, since that session's writer belongs to another caller.
		if (ReferenceEquals(writer, ReplSessionIO.Output) || ReferenceEquals(writer, ReplSessionIO.Error))
		{
			await writer.WriteLineAsync().ConfigureAwait(false);
		}
	}

	private static string Styled(string text, string? style) =>
		string.IsNullOrEmpty(style) ? text : AnsiText.Apply(text, style);
}
