namespace Repl;

/// <param name="Payload">The rendered page.</param>
/// <param name="HasMore">Whether another page can be fetched.</param>
/// <param name="ContainsPresentationChrome">Whether the payload can carry footer lines for the pager to strip.</param>
/// <param name="Layout">
/// The layout the page's transformer declared, or <see langword="null"/> when it declares none and the pager
/// detects its header and footer from the text.
/// </param>
internal sealed record ResultFlowPagerPage(
	string Payload,
	bool HasMore,
	bool ContainsPresentationChrome = true,
	RenderedLayout? Layout = null);
