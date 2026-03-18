namespace Repl.Testing;

/// <summary>
/// Describes the content and color attributes of a single terminal cell.
/// </summary>
/// <param name="Content">The text grapheme at this cell.</param>
/// <param name="FgColor">Foreground color value (interpretation depends on <paramref name="FgMode"/>).</param>
/// <param name="FgMode">Foreground color mode: 0 = default, 1 = 256-color, 2 = RGB.</param>
/// <param name="BgColor">Background color value.</param>
/// <param name="BgMode">Background color mode.</param>
/// <param name="IsInverse">Whether the cell has inverse video applied.</param>
public sealed record CellInfo(
	string Content,
	int FgColor,
	int FgMode,
	int BgColor,
	int BgMode,
	bool IsInverse);
