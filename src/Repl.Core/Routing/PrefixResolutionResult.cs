namespace Repl;

internal sealed class PrefixResolutionResult(
	string[] tokens,
	IReadOnlyList<string>? candidates = null,
	int ambiguousIndex = -1)
{
	public string[] Tokens { get; } = tokens;

	/// <summary>The position in <see cref="Tokens"/> of the word left ambiguous, or -1 when none is.</summary>
	public int AmbiguousIndex { get; } = ambiguousIndex >= -1 && ambiguousIndex < tokens.Length
		? ambiguousIndex
		: throw new ArgumentOutOfRangeException(nameof(ambiguousIndex));

	/// <summary>The word left ambiguous, as typed: expansion stops there, so it is read from the tokens it was given.</summary>
	public string? AmbiguousToken { get; } = ambiguousIndex >= 0 && ambiguousIndex < tokens.Length ? tokens[ambiguousIndex] : null;

	public IReadOnlyList<string> Candidates { get; } = candidates ?? [];

	public bool IsAmbiguous => !string.IsNullOrWhiteSpace(AmbiguousToken) && Candidates.Count > 1;
}
