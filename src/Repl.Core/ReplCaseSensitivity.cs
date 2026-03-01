namespace Repl;

/// <summary>
/// Case-sensitivity behavior for option-token matching.
/// </summary>
public enum ReplCaseSensitivity
{
	/// <summary>
	/// Option tokens must match exact casing.
	/// </summary>
	CaseSensitive = 0,

	/// <summary>
	/// Option tokens are compared ignoring character casing.
	/// </summary>
	CaseInsensitive = 1,
}
