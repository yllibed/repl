namespace Repl;

/// <summary>
/// Defines high-level autocomplete interaction style.
/// </summary>
public enum AutocompletePresentation
{
	/// <summary>
	/// Hybrid mode: first tab completes common prefix, second tab opens suggestion list.
	/// </summary>
	Hybrid = 0,

	/// <summary>
	/// Classic mode: complete and print candidates in plain list style.
	/// </summary>
	Classic = 1,

	/// <summary>
	/// Menu-first mode: first tab opens the suggestion list.
	/// </summary>
	MenuFirst = 2,
}
