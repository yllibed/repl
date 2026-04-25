using System.Globalization;
using Repl.Interaction;

namespace Repl.Spectre;

/// <summary>
/// Spectre-aware interaction presenter that supports explicit output capture
/// when an application temporarily owns the terminal surface.
/// </summary>
public sealed class SpectreInteractionPresenter : IReplInteractionPresenter
{
	private readonly IReplInteractionPresenter _fallback;
	private readonly AsyncLocal<CaptureScope?> _capture = new();

	/// <summary>
	/// Creates a presenter backed by the default console interaction presenter.
	/// </summary>
	public SpectreInteractionPresenter(InteractionOptions options, OutputOptions outputOptions)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(outputOptions);
		_fallback = new ConsoleReplInteractionPresenter(options, outputOptions);
	}

	internal SpectreInteractionPresenter(IReplInteractionPresenter fallback)
	{
		_fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
	}

	/// <summary>
	/// Redirects interaction events to the provided sink for the current async flow.
	/// Dispose the returned scope to restore the previous sink.
	/// </summary>
	public IDisposable BeginCapture(IReplInteractionPresenter sink)
	{
		ArgumentNullException.ThrowIfNull(sink);
		var previous = _capture.Value;
		_capture.Value = new CaptureScope(sink, previous);
		return new CaptureLease(_capture, previous);
	}

	/// <summary>
	/// Redirects interaction events to a plain text writer for the current async flow.
	/// The writer sink never emits ANSI control sequences or OSC progress messages.
	/// </summary>
	public IDisposable BeginCapture(TextWriter writer)
	{
		ArgumentNullException.ThrowIfNull(writer);
		return BeginCapture(new PlainTextCapturePresenter(writer));
	}

	/// <inheritdoc />
	public ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(evt);
		var active = _capture.Value;
		return active?.Sink.PresentAsync(evt, cancellationToken)
			?? _fallback.PresentAsync(evt, cancellationToken);
	}

	private sealed record CaptureScope(
		IReplInteractionPresenter Sink,
		CaptureScope? Previous);

	private sealed class CaptureLease(
		AsyncLocal<CaptureScope?> state,
		CaptureScope? previous) : IDisposable
	{
		private readonly AsyncLocal<CaptureScope?> _state = state;
		private readonly CaptureScope? _previous = previous;
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_state.Value = _previous;
			_disposed = true;
		}
	}

	private sealed class PlainTextCapturePresenter(TextWriter writer) : IReplInteractionPresenter
	{
		private readonly TextWriter _writer = writer;

		public async ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(evt);
			cancellationToken.ThrowIfCancellationRequested();

			switch (evt)
			{
				case ReplStatusEvent status:
					await _writer.WriteLineAsync(status.Text).ConfigureAwait(false);
					break;

				case ReplNoticeEvent notice:
					await _writer.WriteLineAsync(notice.Text).ConfigureAwait(false);
					break;

				case ReplWarningEvent warning:
					await _writer.WriteLineAsync($"Warning: {warning.Text}").ConfigureAwait(false);
					break;

				case ReplProblemEvent problem:
					var header = string.IsNullOrWhiteSpace(problem.Code)
						? $"Problem: {problem.Summary}"
						: $"Problem [{problem.Code}]: {problem.Summary}";
					await _writer.WriteLineAsync(header).ConfigureAwait(false);
					if (!string.IsNullOrWhiteSpace(problem.Details))
					{
						await _writer.WriteLineAsync(problem.Details).ConfigureAwait(false);
					}

					break;

				case ReplPromptEvent prompt:
					await _writer.WriteAsync($"{prompt.PromptText}: ").ConfigureAwait(false);
					break;

				case ReplProgressEvent progress:
					await _writer.WriteLineAsync(FormatProgress(progress)).ConfigureAwait(false);
					break;

				case ReplClearScreenEvent:
					break;
			}
		}

		private static string FormatProgress(ReplProgressEvent progress)
		{
			if (progress.State == ReplProgressState.Clear)
			{
				return string.Empty;
			}

			var percent = progress.ResolvePercent();
			var label = string.IsNullOrWhiteSpace(progress.Label) ? "Progress" : progress.Label;
			if (progress.State == ReplProgressState.Indeterminate)
			{
				return string.IsNullOrWhiteSpace(progress.Details)
					? $"Progress: {label}"
					: $"Progress: {label}: {progress.Details}";
			}

			var prefix = progress.State switch
			{
				ReplProgressState.Warning => "Warning progress",
				ReplProgressState.Error => "Error progress",
				_ => "Progress",
			};

			var text = percent is null
				? $"{prefix}: {label}"
				: $"{prefix}: {label}: {percent.Value.ToString("0.###", CultureInfo.InvariantCulture)}%";
			return string.IsNullOrWhiteSpace(progress.Details)
				? text
				: $"{text}: {progress.Details}";
		}
	}
}
