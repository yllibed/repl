namespace Repl;

/// <summary>
/// Session-scoped Ctrl+C handler that implements double-tap cancellation.
/// <list type="bullet">
///   <item>1st Ctrl+C during a command → cancels the per-command CTS, session continues.</item>
///   <item>2nd Ctrl+C within ~2 s (or Ctrl+C with no active command) → exits the process.</item>
/// </list>
/// Uses <see cref="Console.CancelKeyPress"/> which works universally across terminals,
/// IDEs (Rider, VS Code), SSH sessions, and tmux — unlike Esc-key polling.
/// </summary>
internal sealed class CancelKeyHandler : IDisposable
{
	private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromSeconds(2);

	private CancellationTokenSource? _commandCts;
	private DateTimeOffset _lastCancelPress;
	private readonly Lock _lock = new();
	private readonly bool _hooked;

	internal CancelKeyHandler()
	{
		_hooked = !ReplSessionIO.IsSessionActive;
		if (_hooked)
		{
			Console.CancelKeyPress += OnCancelKeyPress;
		}
	}

	/// <summary>
	/// Activates per-command cancellation. While active, the first Ctrl+C cancels
	/// this CTS instead of terminating the process.
	/// </summary>
	internal void SetCommandCts(CancellationTokenSource? cts)
	{
		lock (_lock)
		{
			_commandCts = cts;
		}
	}

	public void Dispose()
	{
		if (_hooked)
		{
			Console.CancelKeyPress -= OnCancelKeyPress;
		}
	}

	private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
	{
		lock (_lock)
		{
			var now = DateTimeOffset.UtcNow;

			if (_commandCts is { IsCancellationRequested: false })
			{
				// First Ctrl+C during a command → cancel command, keep session alive.
				e.Cancel = true;
				_commandCts.Cancel();
				_lastCancelPress = now;
				Console.Error.WriteLine();
				Console.Error.WriteLine("Press Ctrl+C again to exit.");
				return;
			}

			if (now - _lastCancelPress < DoubleTapWindow)
			{
				// Second Ctrl+C within window → exit (don't set e.Cancel).
				return;
			}

			// Ctrl+C with no active command → exit.
		}
	}
}
