namespace Repl;

internal sealed class OptionParsingResult(
	IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions,
	IReadOnlyList<string> positionalArguments,
	IReadOnlyList<ParseDiagnostic>? diagnostics = null)
{
	public IReadOnlyDictionary<string, IReadOnlyList<string>> NamedOptions { get; } = namedOptions;

	public IReadOnlyList<string> PositionalArguments { get; } = positionalArguments;

	public IReadOnlyList<ParseDiagnostic> Diagnostics { get; } = diagnostics ?? [];

	public bool HasErrors => Diagnostics.Any(d => d.Severity == ParseDiagnosticSeverity.Error);
}
