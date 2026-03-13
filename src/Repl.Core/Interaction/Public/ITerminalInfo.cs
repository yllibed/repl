namespace Repl.Interaction;

/// <summary>
/// Exposes terminal capabilities for custom <see cref="IReplInteractionHandler"/> implementations.
/// Register or resolve via DI to adapt prompts to the current terminal environment.
/// </summary>
public interface ITerminalInfo
{
	/// <summary>
	/// Gets a value indicating whether the terminal supports ANSI escape sequences.
	/// </summary>
	bool IsAnsiSupported { get; }

	/// <summary>
	/// Gets a value indicating whether the process can read individual key presses
	/// (i.e. stdin is not redirected and no hosted session is active).
	/// </summary>
	bool CanReadKeys { get; }

	/// <summary>
	/// Gets the current terminal window size, or <c>null</c> when unavailable.
	/// </summary>
	(int Width, int Height)? WindowSize { get; }

	/// <summary>
	/// Gets the active ANSI color palette, or <c>null</c> when ANSI is disabled.
	/// </summary>
	AnsiPalette? Palette { get; }
}
