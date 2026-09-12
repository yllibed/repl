namespace Repl.Testing;

/// <summary>
/// What one run under a <see cref="ReplProcessSignalHarness"/> ended with.
/// </summary>
public sealed record ReplSignalRunResult
{
	/// <summary>
	/// The status the run resolved to, through the application's own <see cref="ReplOptions.ExitCodes"/>
	/// policy. This is what a real <c>Main</c> would return; the harness does not exit the process, so
	/// nothing acts on it here.
	/// </summary>
	public required int ExitCode { get; init; }

	/// <summary>
	/// How the run ended. A claimed signal reports <see cref="ReplExecutionOutcomeKind.Interrupted"/>;
	/// a run cancelled by its own caller token reports <see cref="ReplExecutionOutcomeKind.Cancelled"/>.
	/// </summary>
	public required ReplExecutionOutcomeKind OutcomeKind { get; init; }

	/// <summary>Everything the run wrote to its output.</summary>
	public required string OutputText { get; init; }

	/// <summary>
	/// Everything the run wrote to its error stream. That includes the framework diagnostics raised
	/// while the run itself was running: a signal bridge that could not be installed, and a
	/// cancellation callback that threw while the run was unwinding.
	/// <para>
	/// It does not include the diagnostics for delivered signals. Those are written from whichever
	/// context delivers the signal — an operating-system callback thread in production, with no session
	/// of its own — so they belong to the delivery and are captured on
	/// <see cref="ReplProcessSignalHarness.DiagnosticText"/> instead.
	/// </para>
	/// </summary>
	public required string DiagnosticText { get; init; }
}
