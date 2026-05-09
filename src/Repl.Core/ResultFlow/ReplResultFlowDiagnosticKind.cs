namespace Repl;

/// <summary>
/// Classifies result-flow paging diagnostic events.
/// </summary>
public enum ReplResultFlowDiagnosticKind
{
	/// <summary>
	/// A page source fetch is about to start.
	/// </summary>
	PageFetchStarting,

	/// <summary>
	/// A page source fetch completed successfully.
	/// </summary>
	PageFetchSucceeded,

	/// <summary>
	/// A page source fetch failed.
	/// </summary>
	PageFetchFailed,
}
