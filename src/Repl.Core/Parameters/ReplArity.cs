namespace Repl.Parameters;

/// <summary>
/// Declares option value cardinality.
/// </summary>
public enum ReplArity
{
	/// <summary>
	/// Option may appear zero or one time.
	/// </summary>
	ZeroOrOne = 0,

	/// <summary>
	/// Option must appear exactly one time.
	/// </summary>
	ExactlyOne = 1,

	/// <summary>
	/// Option may appear zero to many times.
	/// </summary>
	ZeroOrMore = 2,

	/// <summary>
	/// Option must appear one or more times.
	/// </summary>
	OneOrMore = 3,
}
