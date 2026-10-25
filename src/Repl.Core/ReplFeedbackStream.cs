namespace Repl;

/// <summary>
/// Selects the stream that receives interaction feedback: progress, status, notices, warnings, problems
/// and prompt labels.
/// </summary>
public enum ReplFeedbackStream
{
	/// <summary>
	/// The session's error stream when the output is for a program — a machine-readable format
	/// (<see cref="IOutputTransformer.IsMachineReadable"/>), or, in a local console run, a redirected
	/// standard output — and the session's output stream otherwise.
	/// </summary>
	Auto = 0,

	/// <summary>
	/// Always the session's output stream.
	/// </summary>
	Output = 1,

	/// <summary>
	/// Always the session's error stream.
	/// </summary>
	Error = 2,
}
