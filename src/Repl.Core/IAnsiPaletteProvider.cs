namespace Repl;

/// <summary>
/// Produces ANSI palettes for a given terminal theme mode.
/// </summary>
public interface IAnsiPaletteProvider
{
	/// <summary>
	/// Creates a palette for the requested theme mode.
	/// </summary>
	/// <param name="themeMode">Requested theme mode.</param>
	/// <returns>The palette to use for ANSI rendering.</returns>
	AnsiPalette Create(ThemeMode themeMode);
}
