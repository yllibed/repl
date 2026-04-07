namespace Repl;

/// <summary>
/// Default <see cref="ITerminalInfo"/> implementation backed by <see cref="OutputOptions"/>
/// and runtime console state.
/// </summary>
internal sealed class ConsoleTerminalInfo(OutputOptions? outputOptions) : ITerminalInfo
{
	public bool IsAnsiSupported => outputOptions?.IsAnsiEnabled() ?? false;

	public bool CanReadKeys => !Console.IsInputRedirected && !ReplSessionIO.IsSessionActive;

	public (int Width, int Height)? WindowSize
	{
		get
		{
			if (ReplSessionIO.IsSessionActive && ReplSessionIO.WindowSize is { } sessionSize)
			{
				return sessionSize;
			}

			try
			{
				var w = Console.WindowWidth;
				var h = Console.WindowHeight;
				return w > 0 && h > 0 ? (w, h) : null;
			}
			catch
			{
				return null;
			}
		}
	}

	public AnsiPalette? Palette =>
		outputOptions is not null && outputOptions.IsAnsiEnabled()
			? outputOptions.ResolvePalette()
			: null;
}
