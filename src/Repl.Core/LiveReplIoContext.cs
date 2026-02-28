namespace Repl;

/// <summary>
/// Live <see cref="IReplIoContext"/> view backed by the current <see cref="ReplSessionIO"/> async-local state.
/// </summary>
internal sealed class LiveReplIoContext : IReplIoContext
{
	public TextReader Input => ReplSessionIO.Input;

	public TextWriter Output => ReplSessionIO.CommandOutput;

	public TextWriter Error => ReplSessionIO.Error;

	public bool IsHostedSession => ReplSessionIO.IsSessionActive;

	public string? SessionId => ReplSessionIO.CurrentSessionId;
}
