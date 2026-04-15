namespace Repl;

internal sealed class LiveReplLogContextAccessor : IReplLogContextAccessor
{
	public ReplLogContext Current =>
		new(
			SessionId: ReplSessionIO.CurrentSessionId,
			IsSessionActive: ReplSessionIO.IsSessionActive,
			IsHostedSession: ReplSessionIO.IsHostedSession,
			IsProgrammatic: ReplSessionIO.IsProgrammatic,
			IsProtocolPassthrough: ReplSessionIO.IsProtocolPassthrough,
			TransportName: ReplSessionIO.TransportName,
			RemotePeer: ReplSessionIO.RemotePeer,
			TerminalIdentity: ReplSessionIO.TerminalIdentity,
			Output: ReplSessionIO.Output,
			Error: ReplSessionIO.Error);
}
