namespace Repl;

internal sealed record GlobalInvocationOptions(
	IReadOnlyList<string> RemainingTokens)
{
	public bool HelpRequested { get; init; }

	public bool InteractiveForced { get; init; }

	public bool InteractivePrevented { get; init; }

	public bool LogoSuppressed { get; init; }

	public string? OutputFormat { get; init; }

	public IReadOnlyDictionary<string, string> PromptAnswers { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
