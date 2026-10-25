using System.Globalization;
using System.Runtime.CompilerServices;

namespace Repl;

internal sealed class ConsoleReplInteractionPresenter(
	InteractionOptions options,
	OutputOptions? outputOptions = null) : IReplInteractionPresenter
{
	private const string OscPrefix = "\x1b]9;4;";
	private const string Bell = "\x07";
	private readonly InteractionOptions _options = options;
	private readonly OutputOptions? _outputOptions = outputOptions;

	// Resolved on first use: whether feedback is coloured depends on the stream each piece goes to.
	private readonly Lazy<AnsiPalette>? _palette = outputOptions is null ? null : new(outputOptions.ResolvePalette);
	private readonly Func<OutputOptions?, FeedbackTarget> _resolveTarget = InteractionFeedbackRouting.Resolve;

	// Lets a test stand in for the process's console redirection, which it cannot otherwise control.
	internal ConsoleReplInteractionPresenter(
		InteractionOptions options,
		OutputOptions? outputOptions,
		Func<OutputOptions?, FeedbackTarget> resolveTarget)
		: this(options, outputOptions) =>
		_resolveTarget = resolveTarget;

	// The progress line left open on each writer, keyed by the writer itself: this presenter is shared by
	// every session of the app, and one session's progress must not close, or forget, another's line. The
	// keys are weak so the presenter never keeps the writers of ended sessions alive.
	private readonly ConditionalWeakTable<TextWriter, OpenProgressLine> _openProgressLines = new();

	public async ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(evt);
		cancellationToken.ThrowIfCancellationRequested();
		_options.Observer?.OnInteractionEvent(evt);
		var target = _resolveTarget(_outputOptions);
		var output = target.Writer;
		var palette = _outputOptions is not null && _outputOptions.IsAnsiEnabled(target.IsStreamRedirected)
			? _palette?.Value
			: null;

		switch (evt)
		{
			case ReplStatusEvent status:
				await CloseProgressLinesAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled(status.Text, palette?.StatusStyle)).ConfigureAwait(false);
				break;

			case ReplNoticeEvent notice:
				await CloseProgressLinesAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled(notice.Text, palette?.NoticeStyle)).ConfigureAwait(false);
				break;

			case ReplWarningEvent warning:
				await CloseProgressLinesAsync().ConfigureAwait(false);
				await output.WriteLineAsync(Styled($"Warning: {warning.Text}", palette?.WarningStyle)).ConfigureAwait(false);
				break;

			case ReplProblemEvent problem:
				await CloseProgressLinesAsync().ConfigureAwait(false);
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
				await CloseProgressLinesAsync().ConfigureAwait(false);
				await output.WriteAsync($"{Styled(prompt.PromptText, palette?.PromptStyle)}: ").ConfigureAwait(false);
				break;

			case ReplProgressEvent progress:
				await WriteProgressAsync(progress, target, palette).ConfigureAwait(false);
				break;

			case ReplClearScreenEvent:
				await CloseProgressLinesAsync().ConfigureAwait(false);
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
			await CloseProgressLinesAsync().ConfigureAwait(false);
			await TryWriteAdvancedProgressAsync(progress, target).ConfigureAwait(false);
			return;
		}

		var percent = progress.ResolvePercent();
		var payload = FormatProgress(progress, percent);
		await TryWriteAdvancedProgressAsync(progress, target).ConfigureAwait(false);
		if (!target.IsTerminal || (progress.State == ReplProgressState.Normal && percent is null))
		{
			await CloseProgressLinesAsync().ConfigureAwait(false);
			await output.WriteLineAsync(Styled(payload, ResolveProgressStyle(progress.State, palette))).ConfigureAwait(false);
			return;
		}

		// The session's line on its other stream is closed first; the width to overwrite is that of the line
		// on this writer, if any.
		await CloseProgressLinesAsync(except: output).ConfigureAwait(false);
		var openLength = _openProgressLines.TryGetValue(output, out var openLine) ? openLine.Length : 0;
		var styledPayload = Styled(payload, ResolveProgressStyle(progress.State, palette));
		var paddingWidth = Math.Max(openLength, payload.Length);
		var paddedPayload = palette is not null
			? styledPayload + new string(' ', Math.Max(0, paddingWidth - payload.Length))
			: payload.PadRight(paddingWidth);
		await output.WriteAsync($"\r{paddedPayload}").ConfigureAwait(false);
		_openProgressLines.AddOrUpdate(output, new OpenProgressLine(payload.Length));
		if (progress.State != ReplProgressState.Indeterminate && percent >= 100d)
		{
			await CloseProgressLinesAsync().ConfigureAwait(false);
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
			&& TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(outputOptions, target.IsStreamRedirected);
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

	// Closes the lines open on the current session's own streams, never another session's.
	private async ValueTask CloseProgressLinesAsync(TextWriter? except = null)
	{
		var output = ReplSessionIO.Output;
		var error = ReplSessionIO.Error;
		await CloseProgressLineAsync(output, except).ConfigureAwait(false);
		if (!ReferenceEquals(error, output))
		{
			await CloseProgressLineAsync(error, except).ConfigureAwait(false);
		}
	}

	private async ValueTask CloseProgressLineAsync(TextWriter writer, TextWriter? except)
	{
		if (!ReferenceEquals(writer, except) && _openProgressLines.Remove(writer))
		{
			await writer.WriteLineAsync().ConfigureAwait(false);
		}
	}

	private sealed record OpenProgressLine(int Length);

	private static string Styled(string text, string? style) =>
		string.IsNullOrEmpty(style) ? text : AnsiText.Apply(text, style);
}
