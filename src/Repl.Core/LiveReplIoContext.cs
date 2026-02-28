namespace Repl;

internal sealed class LiveReplIoContext : IReplIoContext
{
	public TextReader Input => ReplSessionIO.Input;

	public TextWriter Output => ReplSessionIO.CommandOutput;

	public TextWriter Error => ReplSessionIO.Error;

	public bool IsHostedSession => ReplSessionIO.IsSessionActive;

	public string? SessionId => ReplSessionIO.CurrentSessionId;
}
