namespace Repl;

public sealed partial class CoreReplApp
{
	private AutocompleteEngine? _autocompleteEngine;
	internal AutocompleteEngine Autocomplete => _autocompleteEngine ??= new(this);
}
