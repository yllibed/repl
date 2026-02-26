namespace Repl;

/// <summary>
/// Shell completion setup options.
/// </summary>
public sealed class ShellCompletionOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether shell completion management is enabled.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Gets or sets shell completion setup mode.
	/// </summary>
	public ShellCompletionSetupMode SetupMode { get; set; } = ShellCompletionSetupMode.Manual;

	/// <summary>
	/// Gets or sets an optional preferred shell override.
	/// When set, runtime detection is bypassed.
	/// </summary>
	public ShellKind? PreferredShell { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether setup prompt should appear only once.
	/// </summary>
	public bool PromptOnce { get; set; } = true;

	/// <summary>
	/// Gets or sets an optional custom state file path.
	/// When null, platform defaults are used.
	/// </summary>
	public string? StateFilePath { get; set; }

	/// <summary>
	/// Gets or sets an optional custom bash profile path.
	/// </summary>
	public string? BashProfilePath { get; set; }

	/// <summary>
	/// Gets or sets an optional custom PowerShell profile path.
	/// </summary>
	public string? PowerShellProfilePath { get; set; }

	/// <summary>
	/// Gets or sets an optional custom zsh profile path.
	/// </summary>
	public string? ZshProfilePath { get; set; }
}
