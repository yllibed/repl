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
	/// </summary>
	public TimeSpan RunTimeout { get; set; } = TimeSpan.FromSeconds(10);

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
