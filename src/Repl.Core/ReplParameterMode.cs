namespace Repl;

/// <summary>
/// Controls whether a handler parameter accepts named options, positional arguments, or both.
/// </summary>
public enum ReplParameterMode
{
	/// <summary>
	/// Parameter can bind from named option first, then positional fallback.
	/// </summary>
	OptionAndPositional = 0,

	/// <summary>
	/// Parameter can bind only from named options.
	/// </summary>
	OptionOnly = 1,

	/// <summary>
	/// Parameter can bind only from positional arguments.
	/// </summary>
	ArgumentOnly = 2,
}
