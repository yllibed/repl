namespace Repl.Terminal;

/// <summary>
/// Controls whether shell-integration lifecycle marks (OSC 133 / OSC 633) are emitted
/// around the interactive prompt and command execution.
/// </summary>
public enum ShellIntegrationMode
{
	/// <summary>Emit marks when the terminal is known to render them (capability or environment detection).</summary>
	Auto,

	/// <summary>Always emit marks on ANSI-capable interactive output.</summary>
	Always,

	/// <summary>Never emit marks.</summary>
	Never,
}
