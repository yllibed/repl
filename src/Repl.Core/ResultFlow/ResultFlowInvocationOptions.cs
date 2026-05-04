namespace Repl;

internal sealed record ResultFlowInvocationOptions(
	int? PageSize = null,
	string? Cursor = null,
	bool AllRequested = false,
	ReplPagerMode? PagerMode = null);
