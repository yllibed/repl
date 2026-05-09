namespace Repl;

internal sealed record ResultFlowPagerPage(
	string Payload,
	bool HasMore,
	bool ContainsPresentationChrome = true);
