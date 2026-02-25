namespace Repl;

/// <summary>
/// Execution channel used when evaluating runtime module presence.
/// </summary>
public enum ReplRuntimeChannel
{
	/// <summary>
	/// One-shot local command-line invocation.
	/// </summary>
	Cli = 0,

	/// <summary>
	/// Local interactive REPL loop.
	/// </summary>
	Interactive = 1,

	/// <summary>
	/// Hosted session (for example websocket/telnet/remote terminal).
	/// </summary>
	Session = 2,
}

