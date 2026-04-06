namespace Repl;

/// <summary>
/// Interactive session configuration.
/// </summary>
public sealed class InteractiveOptions
{
	/// <summary>
	/// Initializes a new instance of the <see cref="InteractiveOptions"/> class.
	/// </summary>
	public InteractiveOptions()
	{
		Autocomplete = new AutocompleteOptions();
	}

	/// <summary>
	/// Gets or sets the prompt text.
	/// </summary>
	public string Prompt { get; set; } = ">";

	/// <summary>
	/// Gets or sets the interactive policy.
	/// </summary>
	public InteractivePolicy InteractivePolicy { get; set; } = InteractivePolicy.Auto;

	/// <summary>
	/// Gets or sets an optional history provider for pluggable persistence.
	/// When set, this provider is used instead of the default in-memory implementation.
	/// </summary>
	public IHistoryProvider? HistoryProvider { get; set; }

	/// <summary>
	/// Gets interactive autocomplete options.
	/// </summary>
	public AutocompleteOptions Autocomplete { get; }
}
