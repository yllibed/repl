namespace Repl.Testing;

/// <summary>
/// The wall-clock timeout both entry points put around a run. The session handle bounds one command,
/// the signal harness bounds one run, and both need the same two things: a source linked to the
/// caller's token, and a way to tell their own deadline from the caller cancelling.
/// </summary>
internal static class ReplTestTimeout
{
	/// <summary>
	/// Whether <paramref name="timeout"/> is a real deadline rather than "no timeout".
	/// </summary>
	internal static bool IsEnabled(TimeSpan timeout) =>
		timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan;

	/// <summary>
	/// A source that fires after <paramref name="timeout"/> and also when the caller cancels, or
	/// <see langword="null"/> when no deadline applies. The caller owns disposal.
	/// </summary>
	internal static CancellationTokenSource? CreateSource(TimeSpan timeout, CancellationToken cancellationToken)
	{
		if (!IsEnabled(timeout))
		{
			return null;
		}

		var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		source.CancelAfter(timeout);
		return source;
	}

	/// <summary>
	/// Whether the deadline is what fired, and not the caller's own token. The distinction decides
	/// whether a cancellation is reported as a timeout or left to propagate as the caller's.
	/// </summary>
	internal static bool Expired(CancellationTokenSource? timeout, CancellationToken cancellationToken) =>
		timeout is not null
		&& timeout.IsCancellationRequested
		&& !cancellationToken.IsCancellationRequested;
}
