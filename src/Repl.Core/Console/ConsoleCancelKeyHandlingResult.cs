namespace Repl;

/// <summary>
/// Describes whether a console cancel-key dispatch had an owner and whether that owner claimed the key.
/// The two places that decide whether to suppress termination act only on
/// <see cref="SuppressProcessTermination"/>, so the other two agree on the outcome. They stay distinct
/// because aggregation across handlers has to tell an owner that intentionally allowed termination from
/// no owner at all, and tests assert that difference.
/// </summary>
internal enum ConsoleCancelKeyHandlingResult
{
	NotHandled,
	SuppressProcessTermination,
	AllowProcessTermination,
}
