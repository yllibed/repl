namespace Repl.Parameters;

/// <summary>
/// Declares alias tokens on enum members for flag-like option aliases.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ReplEnumFlagAttribute : Attribute
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ReplEnumFlagAttribute"/> class.
	/// </summary>
	/// <param name="aliases">Alias tokens, including prefixes.</param>
	public ReplEnumFlagAttribute(params string[] aliases)
	{
		Aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
	}

	/// <summary>
	/// Alias tokens for the enum member.
	/// </summary>
	public string[] Aliases { get; }

	// Nullable enums are not legal attribute named arguments (CS0655), so the optional
	// override exposes a non-nullable property and tracks the unset state in a nullable
	// backing field surfaced through the internal CaseSensitivityOverride property.
	private ReplCaseSensitivity? _caseSensitivity;

	/// <summary>
	/// Optional case-sensitivity override for these aliases.
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
}
