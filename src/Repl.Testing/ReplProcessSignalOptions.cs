namespace Repl.Testing;

/// <summary>
/// Options for one <see cref="ReplProcessSignalHarness"/>.
/// </summary>
public sealed class ReplProcessSignalOptions
{
	/// <summary>
	/// How long a run may take before <see cref="ReplSignalRun.Completion"/> fails with a
	/// <see cref="TimeoutException"/>. A signal test starts a run that blocks until it is cancelled,
	/// so this is what turns "the signal never arrived" into a failing test instead of a hung one.
	/// Defaults to 10 seconds. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable it.
	/// <para>
	/// Measured against the clock, not against the run agreeing to stop: a command that never observes
	/// its cancellation token cannot be interrupted, so the harness abandons it rather than waiting.
	/// The command keeps running — nothing here can kill it — and because it still holds a place in the
	/// process-wide signal epoch, disposal reports it rather than letting later signal tests inherit it.
	/// Disabling the timeout gives that up: a run that never ends then hangs both its completion and the
	/// harness's disposal. That is why it takes <see cref="Timeout.InfiniteTimeSpan"/> and nothing else —
	/// zero and negatives are refused rather than read as "no timeout", so the one setting that can hang
	/// a suite has to be asked for in as many words.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">The value is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
	public TimeSpan RunTimeout
	{
		get;
		set => field = value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
			? value
			: throw new ArgumentOutOfRangeException(
				nameof(value),
				value,
				$"{nameof(RunTimeout)} must be positive, or {nameof(Timeout)}.{nameof(Timeout.InfiniteTimeSpan)} to run without a deadline.");
	} = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Strips ANSI escape sequences and carriage returns from captured text, so an assertion does not
	/// depend on whether the run decided to colour its output. Defaults to <see langword="true"/>.
	/// </summary>
	public bool NormalizeAnsi { get; set; } = true;

	/// <summary>
	/// The platform whose decisions apply. Defaults to <see cref="ReplPlatformProfile.Current"/>.
	/// </summary>
	public ReplPlatformProfile Platform { get; set; } = ReplPlatformProfile.Current;

	/// <summary>
	/// Makes the next registration attempt fail with this exception, so a test can assert that
	/// automatic handling degrades to caller-owned and says so, rather than taking it on trust. No
	/// supported platform refuses a registration on demand, so this is the only way to reach that
	/// path. <see langword="null"/> to let registration proceed normally.
	/// </summary>
	public Exception? RegistrationFault { get; set; }
}
