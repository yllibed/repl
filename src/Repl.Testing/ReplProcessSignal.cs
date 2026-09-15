namespace Repl.Testing;

/// <summary>
/// A process signal a test can deliver. Append-only: a new member may be added, but the meaning of an
/// existing one never changes.
/// </summary>
public enum ReplProcessSignal
{
	/// <summary>
	/// Ctrl+C on a console, SIGINT on Unix. Carries the conventional <c>130</c>.
	/// <para>
	/// Goes through console cancel-key arbitration, so an interactive session that owns the console
	/// keys handles it instead of the standalone signal bridge.
	/// </para>
	/// </summary>
	Interrupt,

	/// <summary>
	/// SIGTERM. Carries the conventional <c>143</c>.
	/// <para>
	/// Unlike <see cref="Interrupt"/> and <see cref="Break"/>, SIGTERM does not participate in the
	/// interactive console-key priority rule: an interactive session does not shield a run from it.
	/// </para>
	/// </summary>
	Terminate,

	/// <summary>
	/// Ctrl+Break, which is a signal only on Windows and is ignored elsewhere. Carries the same
	/// <c>130</c> as <see cref="Interrupt"/>; only the operator-facing name differs.
	/// <para>
	/// Whether it counts follows the platform declared in
	/// <see cref="ReplProcessSignalOptions.Platform"/>, not the platform the test happens to run on.
	/// </para>
	/// </summary>
	Break,
}
