namespace Repl.Remote;

/// <summary>A running terminal owned by one protocol connection.</summary>
public interface IRemoteTerminalSession : IAsyncDisposable
{
	/// <summary>Gets the session's eventual exit code.</summary>
	Task<int> Completion { get; }
	/// <summary>Delivers terminal text and control sequences in arrival order.</summary>
	ValueTask InputAsync(string text, CancellationToken cancellationToken);
	/// <summary>Updates the terminal dimensions.</summary>
	ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
	/// <summary>Interrupts the active command without terminating the serving process.</summary>
	ValueTask InterruptAsync(CancellationToken cancellationToken);
}
