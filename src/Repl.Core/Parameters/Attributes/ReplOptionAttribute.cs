namespace Repl.Parameters;

/// <summary>
/// Configures named option metadata for a handler parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
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

	// Nullable enums are not legal attribute named arguments (CS0655), so the optional
	// overrides expose a non-nullable property and track the unset state in a nullable
	// backing field surfaced through the internal *Override properties.
	private ReplCaseSensitivity? _caseSensitivity;
	private ReplArity? _arity;

	/// <summary>
	/// Optional case-sensitivity override for this option.
	/// Only an explicit assignment overrides the global parsing default.
	/// </summary>
	public ReplCaseSensitivity CaseSensitivity
	{
		get => _caseSensitivity ?? default;
		set => _caseSensitivity = value;
	}

	/// <summary>
	/// Explicit case-sensitivity override, or null to inherit the global default.
	/// </summary>
	internal ReplCaseSensitivity? CaseSensitivityOverride => _caseSensitivity;

	/// <summary>
	/// Optional arity override.
	/// Only an explicit assignment overrides the arity inferred from the parameter shape.
	/// </summary>
	public ReplArity Arity
	{
		get => _arity ?? default;
		set => _arity = value;
	}

	/// <summary>
	/// Explicit arity override, or null to use the arity inferred from the parameter shape.
	/// </summary>
	internal ReplArity? ArityOverride => _arity;
}
