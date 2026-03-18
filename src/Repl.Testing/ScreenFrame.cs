namespace Repl.Testing;

/// <summary>
/// A snapshot of the virtual terminal screen captured after an output write.
/// </summary>
/// <param name="Lines">The visible text lines at the time of capture.</param>
/// <param name="CursorX">The cursor column position.</param>
/// <param name="CursorY">The cursor row position.</param>
public sealed record ScreenFrame(
	IReadOnlyList<string> Lines,
	int CursorX,
	int CursorY);
