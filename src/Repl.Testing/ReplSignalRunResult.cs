namespace Repl.Testing;

/// <summary>
/// What one run under a <see cref="ReplProcessSignalHarness"/> ended with.
/// </summary>
/// <param name="ExitCode">
/// The status the run resolved to, through the application's own <see cref="ReplOptions.ExitCodes"/>
/// policy. This is what a real <c>Main</c> would return; the harness does not exit the process, so
/// nothing acts on it here.
/// </param>
/// <param name="OutcomeKind">
/// How the run ended. A claimed signal reports <see cref="ReplExecutionOutcomeKind.Interrupted"/>;
/// a run cancelled by its own caller token reports <see cref="ReplExecutionOutcomeKind.Cancelled"/>.
/// </param>
/// <param name="OutputText">Everything the run wrote to its output.</param>
/// <param name="DiagnosticText">
/// Everything the run wrote to its error stream. That includes the diagnostic explaining a signal
/// bridge that could not be installed, because installing it is part of starting the run.
/// <para>
/// It does not include the diagnostics for delivered signals. Those are written from whichever
/// context delivers the signal — an operating-system callback thread in production, with no session
/// of its own — so they belong to the delivery and are captured on
/// <see cref="ReplProcessSignalHarness.DiagnosticText"/> instead.
/// </para>
/// </param>
public sealed record ReplSignalRunResult(
	int ExitCode,
	ReplExecutionOutcomeKind OutcomeKind,
	string OutputText,
	string DiagnosticText);
