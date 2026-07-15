namespace Repl.Parameters;

/// <summary>
/// Maps an alias token to an injected option value for a parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class ReplValueAliasAttribute : Attribute
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ReplValueAliasAttribute"/> class.
	/// </summary>
	/// <param name="token">Alias token, including prefix.</param>
	/// <param name="value">Injected option value.</param>
	public ReplValueAliasAttribute(string token, string value)
	{
		Token = string.IsNullOrWhiteSpace(token)
			? throw new ArgumentException("Token cannot be empty.", nameof(token))
			: token;
		Value = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>
	/// Alias token, including prefix.
	/// </summary>
	public string Token { get; }

	/// <summary>
	/// Injected option value.
	/// </summary>
	public string Value { get; }

	// Nullable enums are not legal attribute named arguments (CS0655), so the optional
	// override exposes a non-nullable property and tracks the unset state in a nullable
	// backing field surfaced through the internal CaseSensitivityOverride property.
	private ReplCaseSensitivity? _caseSensitivity;

	/// <summary>
	/// Optional case-sensitivity override for this alias.
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
