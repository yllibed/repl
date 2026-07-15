namespace Repl.Parameters;

/// <summary>
/// Configures positional argument metadata for a handler parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ReplArgumentAttribute : Attribute
{
	/// <summary>
	/// Binding mode for the parameter.
	/// </summary>
	public ReplParameterMode Mode { get; set; } = ReplParameterMode.OptionAndPositional;
}
