namespace Repl;

/// <summary>
/// Shell kind used by shell completion setup and detection logic.
/// </summary>
public enum ShellKind
{
	/// <summary>
	/// Shell could not be determined confidently.
	/// </summary>
	Unknown = 0,

	/// <summary>
	/// Shell is recognized but not supported by the current completion bridge.
	/// </summary>
	Unsupported = 1,

	/// <summary>
	/// GNU Bash shell.
	/// </summary>
	Bash = 2,

	/// <summary>
	/// PowerShell shell (<c>pwsh</c> or Windows PowerShell).
	/// </summary>
	PowerShell = 3,

	/// <summary>
	/// Z shell (<c>zsh</c>).
	/// </summary>
	Zsh = 4,
}
