namespace Repl;

/// <summary>
/// Parameter binding configuration.
/// </summary>
public sealed class BindingOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether conversion errors should be aggregated.
	/// </summary>
	public bool AggregateConversionErrors { get; set; } = true;
}