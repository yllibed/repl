namespace Repl.Terminal;

/// <summary>
/// Options for the terminal-integration layer enabled by <c>UseTerminalIntegration</c>.
/// Additional integration intents (hyperlinks, notifications, theme probing) will be
/// added here as they are implemented.
/// </summary>
public sealed class TerminalIntegrationOptions
{
	/// <summary>
	/// Gets or sets how shell-integration lifecycle marks are emitted in interactive mode.
	/// </summary>
	public ShellIntegrationMode ShellIntegration { get; set; } = ShellIntegrationMode.Auto;
}
