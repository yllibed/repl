namespace Repl;

internal sealed record ParseDiagnostic(
	ParseDiagnosticSeverity Severity,
	string Message,
	string? Token = null,
	string? Suggestion = null);
