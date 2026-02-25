namespace Repl;

/// <summary>
/// Event arguments for window size change notifications.
/// </summary>
public sealed class WindowSizeEventArgs(int width, int height) : EventArgs
{
	/// <summary>Terminal width in columns.</summary>
	public int Width { get; } = width;

	/// <summary>Terminal height in rows.</summary>
	public int Height { get; } = height;
}
