namespace Repl;

internal readonly record struct HumanRenderSettings(
	int Width,
	bool UseAnsi,
	AnsiPalette Palette);
