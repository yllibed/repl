namespace Repl;

/// <summary>
/// Terminal-integration extensions for the core REPL app.
/// </summary>
public static class ReplTerminalIntegrationExtensions
{
	/// <summary>
	/// Enables the terminal-integration layer. In interactive mode the prompt and command
	/// lifecycle are marked with shell-integration sequences (OSC 133, or OSC 633 under
	/// VS Code), unlocking command navigation and decorations in capable terminals.
	/// Emission is capability-gated; raw escape sequences are never exposed to handlers.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <param name="configure">Optional integration settings.</param>
	/// <returns>The same app instance.</returns>
	public static CoreReplApp UseTerminalIntegration(
		this CoreReplApp app,
		Action<TerminalIntegrationOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new TerminalIntegrationOptions();
		configure?.Invoke(options);
		app.OptionsSnapshot.TerminalIntegration = options;
		return app;
	}
}
