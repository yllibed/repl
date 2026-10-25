namespace Repl;

/// <summary>
/// A command that ran to completion and whose result reports failures — tests that failed, records
/// that were rejected — as opposed to a command that failed to run. The payload is rendered as the
/// command's result; the exit code comes from <see cref="ExitCodeOptions.CompletedWithFailures"/>.
/// A type that also implements <see cref="IExitResult"/> is classified as an explicit exit.
/// </summary>
public interface ICompletedWithFailuresResult
{
	/// <summary>
	/// Gets the optional payload rendered through the configured output transformer, typically the
	/// report that names the failures.
	/// </summary>
	object? Payload { get; }
}
