namespace Repl;

/// <summary>
/// Maps an alias token to an injected option value for a parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true, Inherited = true)]
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

	/// <summary>
	/// Optional case-sensitivity override for this alias.
	/// </summary>
	public ReplCaseSensitivity? CaseSensitivity { get; set; }
}
