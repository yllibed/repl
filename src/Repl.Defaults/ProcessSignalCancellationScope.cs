namespace Repl;

/// <summary>
/// Represents one disposable lease in <see cref="ProcessSignalCoordinator"/>'s process-wide
/// ownership epoch. A first claimed signal records its conventional exit code and reserves a
/// cancellation-delivery task before callbacks start outside the coordinator gate. Disposal
/// withdraws the active lease, drains that task, and only then disposes the linked token source.
/// </summary>
internal sealed class ProcessSignalCancellationScope : IAsyncDisposable
{
	private readonly CancellationTokenSource _linkedCancellation;
	private readonly Lock _gate = new();
	private Task _cancellationTask = Task.CompletedTask;
	private int? _exitCode;
	private bool _disposed;
	private int _disposeStarted;

	public ProcessSignalCancellationScope(CancellationToken cancellationToken)
	{
		_linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		try
		{
			ProcessSignalCoordinator.Register(this);
		}
		catch
		{
			_linkedCancellation.Dispose();
			throw;
		}
	}

	public CancellationToken Token => _linkedCancellation.Token;

	public int? ExitCode
	{
		get
		{
			lock (_gate)
			{
				return _exitCode;
			}
		}
	}

	public ValueTask DisposeAsync() => DisposeCoreAsync(afterWinningDisposal: null);

	internal ValueTask DisposeForTestingAsync(Action afterWinningDisposal)
	{
		ArgumentNullException.ThrowIfNull(afterWinningDisposal);
		return DisposeCoreAsync(afterWinningDisposal);
	}

	private async ValueTask DisposeCoreAsync(Action? afterWinningDisposal)
	{
		if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
		{
			return;
		}

		afterWinningDisposal?.Invoke();
		try
		{
			var cancellationCallbackException = await ProcessSignalCoordinator.UnregisterAsync(this)
				.ConfigureAwait(false);
			if (cancellationCallbackException is not null)
			{
				ProcessSignalCoordinator.WriteDiagnostic(
					"A process-signal cancellation callback failed after the signal exit policy "
					+ $"was established: {cancellationCallbackException}");
			}
		}
		finally
		{
			_linkedCancellation.Dispose();
		}
	}

	internal Action? PrepareSignalCancellation(int exitCode)
	{
		lock (_gate)
		{
			if (_disposed || _exitCode is not null)
			{
				return null;
			}

			_exitCode = exitCode;
			// Reserve the eventual CancelAsync task under this gate so DisposeAsync cannot miss it.
			// The outer TCS is completed only after the process coordinator gate is released, which
			// guarantees that no consumer cancellation callback runs while either gate is held.
			var cancellationTaskSource = new TaskCompletionSource<Task>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			_cancellationTask = cancellationTaskSource.Task.Unwrap();
			return () =>
			{
				Task cancellationTask;
				try
				{
					cancellationTask = _linkedCancellation.CancelAsync();
				}
				catch (Exception ex)
				{
					cancellationTask = Task.FromException(ex);
				}

				cancellationTaskSource.TrySetResult(cancellationTask);
			};
		}
	}

	internal Task MarkDisposedAndGetCancellationTaskAsync()
	{
		lock (_gate)
		{
			_disposed = true;
#pragma warning disable VSTHRD003 // The coordinator needs the reserved cancellation task so disposal can drain it.
			return _cancellationTask;
#pragma warning restore VSTHRD003
		}
	}
}
