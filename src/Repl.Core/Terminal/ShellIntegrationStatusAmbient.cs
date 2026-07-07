namespace Repl;

/// <summary>
/// Ambient, session-scoped publication of the shell-integration autodetection outcome.
/// The interactive loop opens a slot at loop scope — so the async-local flows into
/// command handlers — and the mark emitter updates the slot at each per-cycle
/// re-resolution. <see cref="IReplSessionInfo.ShellIntegrationStatus"/> reads it.
/// </summary>
internal static class ShellIntegrationStatusAmbient
{
	/// <summary>
	/// Mutable holder shared between the loop scope and its descendants: the async-local
	/// captures the slot reference once, and the emitter mutates its content from child
	/// contexts (an async-local value set inside a child would not flow back up).
	/// </summary>
	internal sealed class Slot
	{
		public string? Status { get; set; }
	}

	private static readonly AsyncLocal<Slot?> s_current = new();

	public static string? Current => s_current.Value?.Status;

	/// <summary>Opens a fresh slot for the current async scope and returns it for the emitter to update.</summary>
	public static Slot Open()
	{
		var slot = new Slot();
		s_current.Value = slot;
		return slot;
	}
}
