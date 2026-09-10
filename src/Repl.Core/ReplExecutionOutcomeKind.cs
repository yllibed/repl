namespace Repl;

/// <summary>
/// Classifies how a top-level run ended, independently of the exit code eventually returned.
/// Values are explicit and append-only so consumers can persist or switch on them safely.
/// </summary>
public enum ReplExecutionOutcomeKind
{
	/// <summary>
	/// The handler completed and produced a success-like result (or no result); also an ambient command
	/// (<c>exit</c>, <c>..</c>) that did its job, and a clean interactive session exit.
	/// </summary>
	Success = 0,

	/// <summary>
	/// The invocation rendered help instead of running a command: help request, bare invocation, or
	/// scoped-context help.
	/// </summary>
	Help = 1,

	/// <summary>
	/// The framework refused the invocation before the handler ran: unknown command, ambiguous
	/// prefix, invalid option, unknown output format, or context validation failure.
	/// </summary>
	UsageError = 2,

	/// <summary>
	/// Handler arguments could not be bound: a token failed to convert or was missing, or a value the
	/// binder resolves itself (context value, <c>[FromServices]</c> dependency, typed global options
	/// service) was unavailable.
	/// </summary>
	BindingError = 3,

	/// <summary>
	/// The handler ran and returned a failure result (<c>error</c>, <c>validation</c>, <c>not_found</c>, …).
	/// </summary>
	HandlerError = 4,

	/// <summary>
	/// The handler returned an <see cref="IExitResult"/>; its exit code is used verbatim.
	/// </summary>
	HandlerExitCode = 5,

	/// <summary>
	/// The handler, a middleware, or user code running after binding (validators, banners, output
	/// transformers) threw an exception that the framework reported.
	/// </summary>
	HandlerException = 6,

	/// <summary>
	/// The caller's own <see cref="CancellationToken"/> stopped the run — already cancelled at the call,
	/// during the command, or while hosted services were starting.
	/// <para>
	/// One-shot and interactive runs draw the line differently, deliberately. In a one-shot run only the
	/// caller's token yields this kind: a handler that raises <see cref="OperationCanceledException"/> on
	/// its own account is a <see cref="HandlerException"/>, so a real failure cannot pass for an operator
	/// abort. An interactive session instead treats a <em>command-scoped</em>
	/// <see cref="OperationCanceledException"/> as an abort — Ctrl+C and a self-cancelling handler alike:
	/// it prints <c>Cancelled.</c> and decorates the command-end mark with this kind's code rather than
	/// rendering the exception.
	/// </para>
	/// <para>
	/// Two interactive cases are not this kind at all, and neither carries a code: an abandoned prompt
	/// (empty line, Escape, end of input), and a cancellation of the session's own token — host
	/// shutdown — which propagates out of the loop after closing the cycle with an aborted mark.
	/// </para>
	/// </summary>
	Cancelled = 7,

	/// <summary>
	/// The run was interrupted by a process signal (SIGINT, Ctrl+Break, SIGTERM) that the framework
	/// claimed, which standalone runs opt into through <c>ReplRunOptions.ProcessSignalHandling</c>. The
	/// core pipeline never produces it on its own: an interruption is decided by the signal owner around
	/// the run, and only a clean or cancelled run is reclassified — one that already produced a refusal
	/// or a failure keeps reporting it.
	/// </summary>
	Interrupted = 8,

	/// <summary>
	/// The framework itself failed: an incompatible adapter contract, an unsupported hosting capability,
	/// or a hosted service that could not start or stop. The outcome carries the exception when one
	/// caused it.
	/// </summary>
	FrameworkError = 9,
}
