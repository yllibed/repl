namespace Repl;

/// <summary>
/// Provides terminal window size detection and change notifications.
/// Implementations are transport-specific (DTTERM in-band VT, Telnet NAWS, SSH channel, etc.).
/// </summary>
public interface IWindowSizeProvider
{
	/// <summary>
	/// Detects the current window size. Returns <c>null</c> if the size cannot be determined.
	/// </summary>
	ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct);

	/// <summary>
	/// Raised when the terminal window is resized.
	/// </summary>
	event EventHandler<WindowSizeEventArgs>? SizeChanged;
}
