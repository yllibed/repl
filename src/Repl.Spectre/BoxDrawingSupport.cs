namespace Repl.Spectre;

/// <summary>
/// How much of Spectre's box-drawing repertoire the current output sink can carry.
/// This is the verdict Spectre console creation acts on; expose it in a diagnostics
/// command via <see cref="SpectreTerminalDetection.CurrentBoxDrawingSupport"/> instead
/// of re-probing by hand. Values are explicit and append-only: new tiers get new numbers
/// so already-compiled consumers keep their meaning.
/// </summary>
public enum BoxDrawingSupport
{
	/// <summary>The sink roundtrips rounded glyphs; full Unicode borders render intact.</summary>
	Rounded = 0,

	/// <summary>
	/// Only the square safe-border glyphs survive — a real console on a legacy OEM
	/// codepage, which carries ┌ but not ╭. Spectre's own non-Unicode fallback covers this.
	/// </summary>
	Square = 1,

	/// <summary>
	/// No box glyph survives the sink (ASCII transport, or a redirected local console whose
	/// reader decodes another charset); box drawing is transliterated to ASCII.
	/// </summary>
	Ascii = 2,
}
