namespace Repl;

/// <summary>
/// Names the gate that decided shell-integration mark enablement for a prompt cycle.
/// The members after <see cref="Enabled"/> are listed in evaluation order — the first
/// failing gate wins — so a wrong on/off decision can be triaged exactly. Mirrors the
/// troubleshooting table in docs/terminal-shell-integration.md.
/// </summary>
internal enum ShellIntegrationGate
{
	/// <summary>All gates passed: marks are emitted this cycle.</summary>
	Enabled,

	/// <summary>The app never called UseTerminalIntegration.</summary>
	NotConfigured,

	/// <summary>A protocol-passthrough scope is active: no mark may reach a protocol stream.</summary>
	ProtocolPassthrough,

	/// <summary>Neither the writer nor the session capabilities support ANSI escape sequences.</summary>
	AnsiUnsupported,

	/// <summary>Local console output is redirected (no terminal on the other end).</summary>
	OutputRedirected,

	/// <summary><see cref="ShellIntegrationMode.Never"/> was configured.</summary>
	ModeNever,

	/// <summary>Auto mode in a hosted session whose client has not advertised mark support.</summary>
	SessionNotAdvertising,

	/// <summary>Auto mode on a local terminal the environment classifier does not recognize.</summary>
	EnvironmentUnknown,
}
