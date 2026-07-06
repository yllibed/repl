namespace Repl.Terminal;

/// <summary>
/// Options for the terminal-integration layer enabled by <c>UseTerminalIntegration</c>.
/// </summary>
public sealed class TerminalIntegrationOptions
{
	/// <summary>
	/// Gets or sets how shell-integration lifecycle marks are emitted in interactive mode.
	/// The value is re-read at the start of every prompt cycle, so changing it at runtime
	/// takes effect on the next prompt.
	/// </summary>
	public ShellIntegrationMode ShellIntegration { get; set; } = ShellIntegrationMode.Auto;
}
