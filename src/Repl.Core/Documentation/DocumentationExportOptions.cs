namespace Repl.Documentation;

/// <summary>
/// Options for the opt-in documentation export ambient command.
/// </summary>
public sealed class DocumentationExportOptions
{
	/// <summary>
	/// Gets or sets the command route used for documentation export.
	/// </summary>
	public string CommandRoute { get; set; } = "doc export";

	/// <summary>
	/// Gets or sets a value indicating whether the command is hidden by default.
	/// </summary>
	public bool HiddenByDefault { get; set; } = true;
}
