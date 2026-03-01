namespace Repl;

/// <summary>
/// Configures named option metadata for a handler parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class ReplOptionAttribute : Attribute
{
	/// <summary>
	/// Canonical option name without prefix.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>
	/// Additional option aliases as full tokens (for example: <c>--mode</c>, <c>-m</c>).
	/// </summary>
	public string[] Aliases { get; set; } = [];

	/// <summary>
	/// Reverse aliases as full tokens (for example: <c>--no-verbose</c>).
	/// </summary>
	public string[] ReverseAliases { get; set; } = [];

	/// <summary>
	/// Binding mode for the parameter.
	/// </summary>
	public ReplParameterMode Mode { get; set; } = ReplParameterMode.OptionAndPositional;

	/// <summary>
	/// Optional case-sensitivity override for this option.
	/// </summary>
	public ReplCaseSensitivity? CaseSensitivity { get; set; }

	/// <summary>
	/// Optional arity override.
	/// </summary>
	public ReplArity? Arity { get; set; }
}
