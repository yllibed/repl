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

	/// <summary>
	/// Gets the registered custom ambient commands.
	/// </summary>
	internal Dictionary<string, AmbientCommandDefinition> CustomCommands { get; } =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Registers a custom ambient command available in all interactive scopes.
	/// </summary>
	/// <param name="name">Command name (matched case-insensitively).</param>
	/// <param name="handler">Handler delegate with injectable parameters.</param>
	/// <param name="description">Optional description shown in help output.</param>
	/// <returns>This instance for fluent chaining.</returns>
	public AmbientCommandOptions MapAmbient(string name, Delegate handler, string? description = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(handler);
		CustomCommands[name] = new AmbientCommandDefinition
		{
			Name = name,
			Description = description,
			Handler = handler,
		};
		return this;
	}
}
