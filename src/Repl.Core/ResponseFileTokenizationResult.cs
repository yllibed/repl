namespace Repl;

internal sealed record ResponseFileTokenizationResult(
	IReadOnlyList<string> Tokens,
	bool HasTrailingEscape);
