namespace Repl;

/// <summary>
/// Receives structured diagnostics for result-flow paging operations.
/// </summary>
public interface IReplResultFlowDiagnostics
{
	/// <summary>
	/// Observes a result-flow diagnostic event.
	/// </summary>
	/// <param name="diagnostic">Diagnostic payload.</param>
	void OnDiagnostic(ReplResultFlowDiagnostic diagnostic);
}
