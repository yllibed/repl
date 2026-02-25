namespace Repl;

internal sealed class DefaultAnsiPaletteProvider : IAnsiPaletteProvider
{
	private static readonly AnsiPalette DarkPalette = new(
		SectionStyle: "\u001b[38;5;117m",
		TableHeaderStyle: "\u001b[1;38;5;221m",
		CommandStyle: "\u001b[38;5;153m",
		DescriptionStyle: "\u001b[38;5;250m",
		JsonPropertyStyle: "\u001b[38;5;81m",
		JsonStringStyle: "\u001b[38;5;114m",
		JsonNumberStyle: "\u001b[38;5;221m",
		JsonKeywordStyle: "\u001b[38;5;213m",
		JsonPunctuationStyle: "\u001b[38;5;244m",
		StatusStyle: "\u001b[38;5;244m",
		PromptStyle: "\u001b[38;5;117m",
		ProgressStyle: "\u001b[38;5;244m",
		BannerStyle: "\u001b[38;5;109m",
		AutocompleteCommandStyle: "\u001b[38;5;153m",
		AutocompleteContextStyle: "\u001b[38;5;117m",
		AutocompleteParameterStyle: "\u001b[38;5;186m",
		AutocompleteAmbiguousStyle: "\u001b[38;5;222m",
		AutocompleteErrorStyle: "\u001b[38;5;203m",
		AutocompleteHintLabelStyle: "\u001b[38;5;244m");

	private static readonly AnsiPalette LightPalette = new(
		SectionStyle: "\u001b[38;5;25m",
		TableHeaderStyle: "\u001b[1;38;5;52m",
		CommandStyle: "\u001b[38;5;19m",
		DescriptionStyle: "\u001b[38;5;238m",
		JsonPropertyStyle: "\u001b[38;5;24m",
		JsonStringStyle: "\u001b[38;5;28m",
		JsonNumberStyle: "\u001b[38;5;130m",
		JsonKeywordStyle: "\u001b[38;5;90m",
		JsonPunctuationStyle: "\u001b[38;5;240m",
		StatusStyle: "\u001b[38;5;240m",
		PromptStyle: "\u001b[38;5;25m",
		ProgressStyle: "\u001b[38;5;240m",
		BannerStyle: "\u001b[38;5;66m",
		AutocompleteCommandStyle: "\u001b[38;5;19m",
		AutocompleteContextStyle: "\u001b[38;5;24m",
		AutocompleteParameterStyle: "\u001b[38;5;94m",
		AutocompleteAmbiguousStyle: "\u001b[38;5;130m",
		AutocompleteErrorStyle: "\u001b[38;5;160m",
		AutocompleteHintLabelStyle: "\u001b[38;5;240m");

	public AnsiPalette Create(ThemeMode themeMode) =>
		themeMode == ThemeMode.Light ? LightPalette : DarkPalette;
}
