using Repl.Rendering;

namespace Repl.TerminalGui;

/// <summary>
/// Bridges Terminal.Gui views to the REPL engine.
/// Creates a <see cref="StreamedReplHost"/> internally and wires
/// <see cref="ReplOutputView"/> as the output and <see cref="ReplInputView"/> as the input source.
/// Like xterm.js bridges a WebSocket session to a browser &lt;div&gt;.
/// </summary>
public sealed class ReplSession : IDisposable
{
	private readonly ReplApp _app;
	private readonly ReplOutputView _outputView;
	private readonly ReplInputView _inputView;
	private readonly TerminalGuiTextWriter _writer;
	private StreamedReplHost? _host;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplSession"/> class.
	/// </summary>
	/// <param name="app">The configured REPL application.</param>
	/// <param name="outputView">The output view to render command results.</param>
	/// <param name="inputView">The input view for command entry.</param>
	/// <param name="historyProvider">Optional shared history provider for command history persistence.</param>
	public ReplSession(
		ReplApp app,
		ReplOutputView outputView,
		ReplInputView inputView,
		IHistoryProvider? historyProvider = null)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(outputView);
		ArgumentNullException.ThrowIfNull(inputView);

		_app = app;
		_outputView = outputView;
		_inputView = inputView;
		_writer = new TerminalGuiTextWriter(outputView);

		if (historyProvider is not null)
		{
			_inputView.SetHistoryProvider(historyProvider);
		}

		_inputView.CommandSubmitted += OnCommandSubmitted;
	}

	private void OnCommandSubmitted(object? sender, CommandSubmittedEventArgs e)
	{
		_host?.EnqueueInput(e.Command + "\n");
	}

	/// <summary>
	/// Runs the REPL session inside a Terminal.Gui application.
	/// The REPL runs on a background task while Terminal.Gui runs on the calling thread.
	/// </summary>
	/// <param name="window">The Terminal.Gui window to run (must contain the output and input views).</param>
	/// <param name="options">Optional REPL run options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The REPL exit code.</returns>
#pragma warning disable CS0618 // Static Application API — Terminal.Gui v2 develop
	public async Task<int> RunAsync(
		Window window,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(window);

		_host = new StreamedReplHost(_writer)
		{
			TransportName = "terminal-gui",
		};

		// Configure as ANSI-capable.
		var runOptions = options ?? new ReplRunOptions
		{
			AnsiSupport = AnsiMode.Always,
			TerminalOverrides = new TerminalSessionOverrides
			{
				AnsiSupported = true,
			},
		};

		var viewport = _outputView.Viewport;

		if (viewport.Width > 0 && viewport.Height > 0)
		{
			_host.UpdateWindowSize(viewport.Width, viewport.Height);
		}

		// Start the REPL session on a background task.
		var sessionTask = Task.Run(
			async () =>
			{
				try
				{
					return await _host.RunSessionAsync(_app, runOptions, cancellationToken)
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return 0;
				}
				finally
				{
					Application.Invoke(() => Application.RequestStop(window));
				}
			},
			cancellationToken);

		// Focus the input field.
		_inputView.FocusInput();

		// Run Terminal.Gui on the main thread (blocks until RequestStop).
		Application.Run(window);

		// If Terminal.Gui exits before the REPL, signal completion.
		_host.Complete();

		return await sessionTask.ConfigureAwait(false);
	}
#pragma warning restore CS0618

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_inputView.CommandSubmitted -= OnCommandSubmitted;

		if (_host is not null)
		{
			_host.Complete();
			_ = _host.DisposeAsync().AsTask();
		}
	}

}
