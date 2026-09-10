namespace Repl.Testing;

/// <summary>
/// A run started by <see cref="ReplProcessSignalHarness.StartRunAsync"/> and still in flight. Its
/// signal scope is already registered by the time the start call returns, so a signal delivered from
/// here on reaches it rather than falling through as inert.
/// </summary>
public sealed class ReplSignalRun
{
	internal ReplSignalRun(string commandLine, Task<ReplSignalRunResult> completion)
	{
		CommandLine = commandLine;
		Completion = completion;
	}

	/// <summary>The command line this run was started with.</summary>
	public string CommandLine { get; }

	/// <summary>
	/// Completes when the run finishes. Faults with <see cref="TimeoutException"/> when the run
	/// outlives <see cref="ReplProcessSignalOptions.RunTimeout"/>, which is what a signal that never
	/// arrived looks like.
	/// </summary>
	public Task<ReplSignalRunResult> Completion { get; }
}
