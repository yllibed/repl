namespace Repl;

/// <summary>
/// Forces a parameter to resolve from context hierarchy values.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromContextAttribute : Attribute
{
	/// <summary>
	/// Gets or sets a value indicating whether all matching ancestor context objects should be bound.
	/// </summary>
	public bool All { get; init; }
}
