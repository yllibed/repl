namespace Repl.Parameters;

/// <summary>
/// Configures positional argument metadata for a handler parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class ReplArgumentAttribute : Attribute
{
	/// <summary>
	/// Optional explicit position for positional binding.
	/// </summary>
	public int? Position { get; set; }

	/// <summary>
	/// Binding mode for the parameter.
	/// </summary>
	public ReplParameterMode Mode { get; set; } = ReplParameterMode.OptionAndPositional;
}
