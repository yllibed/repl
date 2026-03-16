namespace Repl.TerminalGui;

/// <summary>
/// Event arguments for the <see cref="ReplInputView.CommandSubmitted"/> event.
/// </summary>
/// <param name="Command">The command text that was submitted.</param>
public sealed class CommandSubmittedEventArgs(string Command) : EventArgs
{
	/// <summary>
	/// Gets the command text that was submitted.
	/// </summary>
	public string Command { get; } = Command;
}
