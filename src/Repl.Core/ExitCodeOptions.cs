namespace Repl;

/// <summary>
/// Maps <see cref="ReplExecutionOutcomeKind"/> categories to process exit codes and exposes a final
/// interception point. Applies to top-level runs only; nested sub-invocations use the built-in defaults.
/// </summary>
public sealed class ExitCodeOptions
{
	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.Success"/>. Default <c>0</c>.
	/// </summary>
	public int Success { get; set; }

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.Help"/>. Default <c>0</c>;
	/// set it non-zero when a bare invocation must fail in scripted pipelines.
	/// </summary>
	public int Help { get; set; }

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.UsageError"/>. Default <c>2</c>.
	/// </summary>
	public int UsageError { get; set; } = 2;

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.BindingError"/>. Default <c>2</c>.
	/// </summary>
	public int BindingError { get; set; } = 2;

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.HandlerError"/>. Default <c>1</c>.
	/// </summary>
	public int HandlerError { get; set; } = 1;

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.HandlerException"/>. Default <c>1</c>.
	/// </summary>
	public int HandlerException { get; set; } = 1;

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.Cancelled"/> — a run stopped
	/// through the caller's own <see cref="CancellationToken"/>, whether during the command or while
	/// hosted services were starting. When <see langword="null"/> (the default) the
	/// <see cref="OperationCanceledException"/> propagates to the caller instead of being converted,
	/// unless a <see cref="Resolver"/> is set, which also opts in to observing cancellation — the
	/// resolver is then handed <c>130</c>, the shell convention for <c>128 + SIGINT</c>. A handler that
	/// raises <see cref="OperationCanceledException"/> without the caller having asked for cancellation
	/// is a failure, reported as <see cref="ReplExecutionOutcomeKind.HandlerException"/>.
	/// </summary>
	public int? Cancelled { get; set; }

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.Interrupted"/> — a process
	/// signal (SIGINT, Ctrl+Break, SIGTERM) turned into a cooperative shutdown by a process-signal
	/// handler — <c>ReplRunOptions.ProcessSignalHandling</c> in automatic mode claims the signal and
	/// reports the run as interrupted.
	/// <para>
	/// When <see langword="null"/> (the default) the conventional <c>128 + signal</c> code the claimed
	/// signal carries is used — <c>130</c> for SIGINT and Ctrl+Break, <c>143</c> for SIGTERM — falling
	/// back to <c>130</c> when none is supplied. Set this to publish a single code for every signal
	/// instead. A run that had already produced its own refusal or failure keeps reporting that instead
	/// of the interruption; only a clean or cancelled run is reclassified.
	/// </para>
	/// </summary>
	public int? Interrupted { get; set; }

	/// <summary>
	/// Gets or sets the exit code for <see cref="ReplExecutionOutcomeKind.FrameworkError"/>. Default <c>1</c>.
	/// </summary>
	public int FrameworkError { get; set; } = 1;

	/// <summary>
	/// Gets or sets a final interception hook invoked with the structured outcome (whose
	/// <see cref="ReplExecutionOutcome.ExitCode"/> already reflects this table); its return value becomes
	/// the exit code.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Invoked once per top-level run that returns an exit code — including a run that entered and left
	/// an interactive session — with <see cref="ReplExecutionOutcome.Scope"/> set to
	/// <see cref="ReplExitCodeScope.Process"/>. Setting this hook is itself enough to make a
	/// caller-token cancellation observable: it is then reported as
	/// <see cref="ReplExecutionOutcomeKind.Cancelled"/> instead of letting the
	/// <see cref="OperationCanceledException"/> propagate, even with <see cref="Cancelled"/> left unset.
	/// It is not invoked for a run that ends by propagating any other exception, nor for nested
	/// sub-invocations such as MCP tool calls, which keep the built-in defaults.
	/// </para>
	/// <para>
	/// An interactive session additionally invokes it once per committed command whose shell-integration
	/// command-end mark actually carries a code, with <see cref="ReplExecutionOutcome.Scope"/> set to
	/// <see cref="ReplExitCodeScope.ShellIntegrationMark"/>. That never happens with shell integration
	/// off (the default), for a protocol-passthrough command, or for an abandoned prompt cycle (empty
	/// line, Escape, end of input, session cancellation) — those emit no code at all. Side effects in the
	/// hook must therefore not assume a one-to-one relationship with a process exit.
	/// </para>
	/// <para>
	/// The hook must not throw: an exception from it is swallowed and the table-mapped code is used
	/// instead. One diagnostic line is written to the session's error stream on a best-effort basis — the
	/// fallback code is the contract, so a failing error stream cannot turn a resolver bug into a failed
	/// run either.
	/// </para>
	/// </remarks>
	public Func<ReplExecutionOutcome, int>? Resolver { get; set; }

	// Kept private so no friend assembly can mutate the process-wide defaults used by sub-invocations.
	private static readonly ExitCodeOptions s_defaults = new();

	/// <summary>
	/// The shell convention for a command stopped by SIGINT (<c>128 + 2</c>). Used when a cancellation or
	/// an interruption carries no conventional code of its own and none is configured for its kind, so an
	/// aborted run is never reported with the same code as a handler failure.
	/// </summary>
	internal const int ConventionalInterruptedExitCode = 130;

	/// <summary>
	/// Maps a kind with the built-in defaults, ignoring any application configuration.
	/// </summary>
	internal static int MapDefault(ReplExecutionOutcomeKind kind, int? carriedExitCode) =>
		s_defaults.Map(kind, carriedExitCode);

	/// <summary>
	/// Maps a kind to its configured code. <paramref name="carriedExitCode"/> is the code the outcome
	/// itself carries: the <see cref="IExitResult"/> code, or the conventional <c>128 + signal</c> code
	/// for a cancellation or an interruption, which <see cref="Cancelled"/> and
	/// <see cref="Interrupted"/> override when set. A cancellation or interruption that carries no code
	/// and has none configured falls back to <see cref="ConventionalInterruptedExitCode"/> rather than to
	/// <see cref="FrameworkError"/>, so an aborted run stays distinguishable from a broken one. Codes
	/// should stay within <c>0</c>-<c>255</c>: POSIX <c>wait</c> exposes only the low eight bits to the
	/// parent process.
	/// </summary>
	internal int Map(ReplExecutionOutcomeKind kind, int? carriedExitCode) =>
		kind switch
		{
			ReplExecutionOutcomeKind.Success => Success,
			ReplExecutionOutcomeKind.Help => Help,
			ReplExecutionOutcomeKind.UsageError => UsageError,
			ReplExecutionOutcomeKind.BindingError => BindingError,
			ReplExecutionOutcomeKind.HandlerError => HandlerError,
			ReplExecutionOutcomeKind.HandlerExitCode => carriedExitCode ?? Success,
			ReplExecutionOutcomeKind.HandlerException => HandlerException,
			ReplExecutionOutcomeKind.Cancelled => Cancelled ?? carriedExitCode ?? ConventionalInterruptedExitCode,
			ReplExecutionOutcomeKind.Interrupted => Interrupted ?? carriedExitCode ?? ConventionalInterruptedExitCode,
			_ => FrameworkError,
		};
}
