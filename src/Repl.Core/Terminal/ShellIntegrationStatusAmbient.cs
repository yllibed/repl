namespace Repl;

/// <summary>
/// Ambient, session-scoped publication of the shell-integration autodetection outcome.
/// The interactive loop opens a scope at loop level — so the async-local flows into
/// command handlers — and the mark emitter updates the scope's slot at each per-cycle
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

	/// <summary>
	/// Restores the previously ambient slot on dispose, so a nested interactive session
	/// cannot leak its status into the outer one and a closed scope leaves nothing stale
	/// behind in the current flow.
	/// </summary>
	internal sealed class Scope : IDisposable
	{
		private readonly Slot? _previous;
		private bool _disposed;

		internal Scope(Slot? previous, Slot slot)
		{
			_previous = previous;
			Slot = slot;
		}

		public Slot Slot { get; }

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			s_current.Value = _previous;
		}
	}

	private static readonly AsyncLocal<Slot?> s_current = new();

	public static string? Current => s_current.Value?.Status;

	/// <summary>Opens a fresh slot for the current async scope; dispose to restore the previous one.</summary>
	public static Scope Open()
	{
		var scope = new Scope(s_current.Value, new Slot());
		s_current.Value = scope.Slot;
		return scope;
	}
}
