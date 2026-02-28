namespace Repl;

internal sealed class LiveReplIoContext : IReplIoContext
{
	public TextReader Input => ReplSessionIO.Input;

	public TextWriter Output => ReplSessionIO.Output;

	public TextWriter Error => ReplSessionIO.IsSessionActive ? ReplSessionIO.Output : Console.Error;

	public bool IsHostedSession => ReplSessionIO.IsSessionActive;

	public string? SessionId => ReplSessionIO.CurrentSessionId;
}
