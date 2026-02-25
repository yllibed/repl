namespace Repl;

/// <summary>
/// Live <see cref="IReplSessionInfo"/> implementation that reads from per-session
/// <see cref="ReplSessionIO"/> async-local state.
/// </summary>
internal sealed class LiveSessionInfo : IReplSessionInfo
{
	public (int Width, int Height)? WindowSize => ReplSessionIO.WindowSize;

	public bool AnsiSupported => ReplSessionIO.AnsiSupport ?? false;

	public string? TransportName => ReplSessionIO.TransportName;

	public string? RemotePeer => ReplSessionIO.RemotePeer;

	public TerminalCapabilities TerminalCapabilities => ReplSessionIO.TerminalCapabilities;

	public string? TerminalIdentity => ReplSessionIO.TerminalIdentity;
}
