namespace Repl;

/// <summary>
/// Configures how shell completion installation is handled at runtime.
/// </summary>
public enum ShellCompletionSetupMode
{
	/// <summary>
	/// No automatic setup. Users install/uninstall completion explicitly via commands.
	/// </summary>
	Manual = 0,

	/// <summary>
	/// Prompt once in interactive sessions and offer installation when shell is supported.
	/// </summary>
	Prompt = 1,

	/// <summary>
	/// Attempt installation automatically in interactive sessions when shell is supported.
	/// </summary>
	Auto = 2,
}
