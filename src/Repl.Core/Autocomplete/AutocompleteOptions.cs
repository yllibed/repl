namespace Repl.Autocomplete;

/// <summary>
/// Interactive autocomplete options.
/// </summary>
public sealed class AutocompleteOptions
{
	/// <summary>
	/// Gets or sets autocomplete mode.
	/// </summary>
	public AutocompleteMode Mode { get; set; } = AutocompleteMode.Auto;

	/// <summary>
	/// Gets or sets autocomplete presentation style.
	/// </summary>
	public AutocompletePresentation Presentation { get; set; } = AutocompletePresentation.Hybrid;

	/// <summary>
	/// Gets or sets max number of suggestions rendered in the popup/list.
	/// </summary>
	public int MaxVisibleSuggestions { get; set; } = 8;

	/// <summary>
	/// Gets or sets a value indicating whether completion matching is case-sensitive.
	/// </summary>
	public bool CaseSensitive { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether fuzzy scoring should be enabled.
	/// </summary>
	public bool EnableFuzzyMatching { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether live one-line hints are rendered while typing.
	/// </summary>
	public bool LiveHintEnabled { get; set; } = true;

	/// <summary>
	/// Gets or sets the maximum number of alternatives shown in the live hint line.
	/// </summary>
	public int LiveHintMaxAlternatives { get; set; } = 5;

	/// <summary>
	/// Gets or sets a value indicating whether context alternatives are included in autocomplete.
	/// </summary>
	public bool ShowContextAlternatives { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether invalid alternatives are shown when no selectable match exists.
	/// </summary>
	public bool ShowInvalidAlternatives { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether the active input line is colorized by token kind.
	/// </summary>
	public bool ColorizeInputLine { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether hints and menu entries are colorized.
	/// </summary>
	public bool ColorizeHintAndMenu { get; set; } = true;
}
