namespace Repl;

/// <summary>A result-flow transformer's rendering, with the layout it declares for the pager.</summary>
/// <param name="Text">The rendered text.</param>
/// <param name="Layout">The header and footer <paramref name="Text"/> starts and ends with.</param>
internal sealed record RenderedPayload(string Text, RenderedLayout Layout)
{
	/// <summary>Text with neither a header nor a footer.</summary>
	public static RenderedPayload Plain(string text) => new(text, RenderedLayout.None);

	/// <summary>This payload with <paramref name="footer"/>, one line, declared below it; unchanged when it is blank.</summary>
	public RenderedPayload WithFooterLine(string footer) =>
		string.IsNullOrWhiteSpace(footer)
			? this
			: new RenderedPayload(string.Concat(Text, Environment.NewLine, footer), Layout.WithFooter(1));
}
