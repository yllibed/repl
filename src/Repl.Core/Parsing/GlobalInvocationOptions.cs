namespace Repl;

internal sealed record GlobalInvocationOptions(
	IReadOnlyList<string> RemainingTokens)
{
	public bool HelpRequested { get; init; }

	public bool InteractiveForced { get; init; }

	public bool InteractivePrevented { get; init; }

	public bool LogoSuppressed { get; init; }

	public string? OutputFormat { get; init; }

	public ResultFlowInvocationOptions ResultFlow { get; init; } = new();

	public IReadOnlyDictionary<string, string> PromptAnswers { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyDictionary<string, IReadOnlyList<string>> CustomGlobalNamedOptions { get; init; } =
		new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<ParseDiagnostic> Diagnostics { get; init; } = [];

	public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == ParseDiagnosticSeverity.Error);
}
