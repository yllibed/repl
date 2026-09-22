namespace Repl;

/// <summary>
/// How a <c>Run*</c> call manages the session's dependency-injection scope.
/// </summary>
/// <remarks>
/// Governs the <c>Run*</c> family only. MCP execution never goes through it: an MCP connection is its
/// own session and opens its own scope, so there is nothing for a caller to own there.
/// </remarks>
public enum SessionScopeBehavior
{
	/// <summary>
	/// The run opens one DI scope for the session (the default): <c>Scoped</c> services resolve
	/// per session and scoped disposables are released when the session ends.
	/// </summary>
	/// <remarks>
	/// The scope comes from <c>IServiceScopeFactory</c>, which is a singleton, so a scope opened over a
	/// provider that is itself a scope is a SIBLING rooted at the application root — not a child. Against
	/// a caller's request scope that does not hide their scoped instances, it duplicates them: a second
	/// unit of work alongside the live one. Use <see cref="CallerOwned"/> there.
	/// </remarks>
	PerRun = 0,

	/// <summary>
	/// The caller's service provider already represents the session's scope (for example a
	/// Blazor circuit or per-request scope, or a session owner spanning several one-shot
	/// runs): the run resolves from it directly and never opens a nested scope.
	/// </summary>
	CallerOwned = 1,
}
