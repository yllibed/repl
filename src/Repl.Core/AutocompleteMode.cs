namespace Repl;

/// <summary>
/// Configures interactive autocomplete behavior.
/// </summary>
public enum AutocompleteMode
{
	/// <summary>Disable interactive autocomplete.</summary>
	Off = 0,

	/// <summary>Resolve behavior from terminal capabilities with session overrides.</summary>
	Auto = 1,

	/// <summary>Use conservative text-only autocomplete rendering.</summary>
	Basic = 2,

	/// <summary>Use ANSI-enhanced autocomplete rendering.</summary>
	Rich = 3,
}
