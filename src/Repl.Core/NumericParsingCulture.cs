namespace Repl;

/// <summary>
/// Numeric parsing culture mode.
/// </summary>
public enum NumericParsingCulture
{
	/// <summary>
	/// Parses numerics using <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
	/// </summary>
	Invariant = 0,

	/// <summary>
	/// Parses numerics using <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
	/// </summary>
	Current = 1,
}
