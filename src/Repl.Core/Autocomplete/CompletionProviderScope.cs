namespace Repl;

/// <summary>
/// Controls which completion surfaces may invoke a <see cref="CompletionDelegate"/>.
/// </summary>
public enum CompletionProviderScope
{
	/// <summary>
	/// The provider is invoked by in-process surfaces only: the interactive Tab menu and the
	/// <c>complete</c> ambient command. This is the default because the shell completion
	/// bridge spawns a new process for every completion request and blocks the user's shell
	/// until it answers — a slow provider (network, database) must not run there implicitly.
	/// </summary>
	Interactive = 0,

	/// <summary>
	/// The provider is additionally invoked by the shell completion bridge
	/// (<c>completion __complete</c>). Opt in only when the provider is fast enough for a
	/// blocking shell Tab, such as in-memory or local lookups.
	/// </summary>
	InteractiveAndShell = 1,
}
