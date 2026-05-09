namespace Repl;

/// <summary>
/// Describes a result-flow paging diagnostic event.
/// </summary>
public sealed record ReplResultFlowDiagnostic(
	ReplResultFlowDiagnosticKind Kind,
	string? Cursor,
	int PageSize,
	int? ItemCount = null,
	Exception? Exception = null);
