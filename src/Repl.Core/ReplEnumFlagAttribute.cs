namespace Repl;

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

	/// <summary>
	/// Optional case-sensitivity override for these aliases.
	/// </summary>
	public ReplCaseSensitivity? CaseSensitivity { get; set; }
}
