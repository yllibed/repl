namespace Repl;

/// <summary>
/// How a Run* call manages the session's dependency-injection scope.
/// </summary>
public enum SessionScopeBehavior
{
	/// <summary>
	/// The run opens one DI scope for the session (the default): Scoped services resolve
	/// per session and scoped disposables are released when the session ends.
	/// </summary>
	PerRun = 0,

	/// <summary>
	/// The caller's service provider already represents the session's scope (for example a
	/// Blazor circuit or per-request scope, or a session owner spanning several one-shot
	/// runs): the run resolves from it directly and never opens a nested scope.
	/// </summary>
	CallerOwned = 1,
}
