using System.Runtime.InteropServices;

namespace Repl;

/// <summary>
/// A sub-invocation's exit code together with how the run ended.
/// </summary>
/// <remarks>
/// A host that surfaces a failure to somebody other than the operator needs to know whether the text
/// it is about to show was authored by the handler or rendered from an exception it never meant to
/// report. The exit code alone cannot tell those apart.
/// </remarks>
/// <param name="ExitCode">Resolved process exit code.</param>
/// <param name="Kind">How the run ended.</param>
/// <param name="Failure">
/// Exception that ended the run, when one did. The kind says what happened; only the exception says
/// where it came from, and a host deciding what a remote caller may read needs both.
/// </param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SubInvocationOutcome(
	int ExitCode,
	ReplExecutionOutcomeKind Kind,
	Exception? Failure = null);
