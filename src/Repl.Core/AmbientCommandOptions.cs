namespace Repl;

/// <summary>
/// Ambient command options.
/// </summary>
public sealed class AmbientCommandOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether the <c>exit</c> ambient command is enabled.
	/// </summary>
	public bool ExitCommandEnabled { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether the <c>history</c> ambient command
	/// is shown in interactive help output.
	/// </summary>
	public bool ShowHistoryInHelp { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether the <c>complete</c> ambient command
	/// is shown in interactive help output.
	/// </summary>
	public bool ShowCompleteInHelp { get; set; }
}
