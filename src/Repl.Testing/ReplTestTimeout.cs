namespace Repl.Testing;

/// <summary>
/// The wall-clock timeout both entry points put around a run. The session handle bounds one command,
/// the signal harness bounds one run, and both need the same two things: a source linked to the
/// caller's token, and a way to tell their own deadline from the caller cancelling.
/// </summary>
internal static class ReplTestTimeout
{
	/// <summary>
	/// The largest finite timeout the waiting primitives accept: <see cref="Task.WaitAsync(TimeSpan, TimeProvider)"/>
	/// refuses anything above <c>uint.MaxValue - 1</c> milliseconds. A larger value does not bound a
	/// wait, it fails it — from inside the code whose whole job is to bound.
	/// </summary>
	internal static readonly TimeSpan MaxSupported = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

	/// <summary>
	/// Returns <paramref name="value"/> when it can bound a wait, and throws where the caller set it
	/// otherwise. Positive and within <paramref name="ceiling"/>, or
	/// <see cref="Timeout.InfiniteTimeSpan"/> for no bound at all — nothing in between, because every
	/// other reading of an unusable timeout silently removes a deadline somebody asked for.
	/// </summary>
	/// <param name="value">The value being assigned.</param>
	/// <param name="ceiling">The largest value this particular timeout can carry, which is lower than <see cref="MaxSupported"/> wherever the harness derives a longer wait from it.</param>
	/// <param name="propertyName">The property being assigned, for the message.</param>
	internal static TimeSpan Validated(TimeSpan value, TimeSpan ceiling, string propertyName) =>
		value == Timeout.InfiniteTimeSpan || (value > TimeSpan.Zero && value <= ceiling)
			? value
			: throw new ArgumentOutOfRangeException(
				nameof(value),
				value,
				$"{propertyName} must be positive and at most {ceiling}, or "
				+ $"{nameof(Timeout)}.{nameof(Timeout.InfiniteTimeSpan)} to wait without a deadline.");

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
