namespace Repl.Rendering;

/// <summary>
/// Semantic ANSI style palette used by human-oriented renderers.
/// </summary>
public sealed record AnsiPalette(
	string SectionStyle,
	string TableHeaderStyle,
	string CommandStyle,
	string DescriptionStyle,
	string JsonPropertyStyle = "",
	string JsonStringStyle = "",
	string JsonNumberStyle = "",
	string JsonKeywordStyle = "",
	string JsonPunctuationStyle = "",
	string StatusStyle = "",
	string PromptStyle = "",
	string ProgressStyle = "",
	string BannerStyle = "",
	string AutocompleteCommandStyle = "",
	string AutocompleteContextStyle = "",
	string AutocompleteParameterStyle = "",
	string AutocompleteAmbiguousStyle = "",
	string AutocompleteErrorStyle = "",
	string AutocompleteHintLabelStyle = "");
