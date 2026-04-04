namespace Repl;

internal sealed class PrefixResolutionResult(
	string[] tokens,
	string? ambiguousToken = null,
	IReadOnlyList<string>? candidates = null)
{
	public string[] Tokens { get; } = tokens;

	public string? AmbiguousToken { get; } = ambiguousToken;

	public IReadOnlyList<string> Candidates { get; } = candidates ?? [];

	public bool IsAmbiguous => !string.IsNullOrWhiteSpace(AmbiguousToken) && Candidates.Count > 1;
}
