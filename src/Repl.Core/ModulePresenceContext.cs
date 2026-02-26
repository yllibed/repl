namespace Repl;

/// <summary>
/// Provides runtime context used to evaluate module presence predicates.
/// </summary>
public sealed class ModulePresenceContext
{
	internal ModulePresenceContext(
		IServiceProvider serviceProvider,
		ReplRuntimeChannel channel,
		IReplSessionState sessionState,
		IReplSessionInfo sessionInfo)
	{
		ServiceProvider = serviceProvider;
		Channel = channel;
		SessionState = sessionState;
		SessionInfo = sessionInfo;
	}

	/// <summary>
	/// Gets the runtime channel currently resolving routes.
	/// </summary>
	public ReplRuntimeChannel Channel { get; }

	/// <summary>
	/// Gets mutable per-session state for dynamic presence decisions.
	/// </summary>
	public IReplSessionState SessionState { get; }

	/// <summary>
	/// Gets read-only terminal/session metadata for dynamic presence decisions.
	/// </summary>
	public IReplSessionInfo SessionInfo { get; }

	internal IServiceProvider ServiceProvider { get; }
}
