namespace Repl;

/// <summary>
/// Session-scoped Ctrl+C handler. The first press during a command cancels its CTS and keeps
/// the session alive; a subsequent press, or a press with no active command, exits the process.
/// Registers its claim with <see cref="ConsoleCancelKeyCoordinator"/> so interactive and standalone
/// handlers share one atomic, process-wide ownership decision.
/// </summary>
internal sealed class CancelKeyHandler : IDisposable
{
	private CancellationTokenSource? _commandCts;
	private readonly Lock _lock = new();
	private readonly IDisposable? _registration;
	private int _disposed;

	internal CancelKeyHandler()
	{
		if (!ReplSessionIO.IsSessionActive)
		{
			// The key is irrelevant here: Ctrl+C and Ctrl+Break cancel the active command the same way.
			_registration = ConsoleCancelKeyCoordinator.RegisterInteractive(_ => TryHandleCancelKey());
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
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		_registration?.Dispose();
	}

	internal ConsoleCancelKeyHandlingResult HandleCancelKeyForTesting() => TryHandleCancelKey();

	private ConsoleCancelKeyHandlingResult TryHandleCancelKey()
	{
		lock (_lock)
		{
			if (_commandCts is { IsCancellationRequested: false })
			{
				_commandCts.Cancel();
				ReplSessionIO.Error.WriteLine();
				ReplSessionIO.Error.WriteLine("Press Ctrl+C again to exit.");
				return ConsoleCancelKeyHandlingResult.SuppressProcessTermination;
			}

			// A subsequent press, or a press with no active command, retains
			// the operating-system default instead of handing ownership to a standalone run.
			return ConsoleCancelKeyHandlingResult.AllowProcessTermination;
		}
	}
}
