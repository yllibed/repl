namespace Repl;

/// <summary>
/// Documentation export extensions for the DI-enabled REPL app.
/// </summary>
public static class ReplAppDocumentationExtensions
{
	/// <summary>
	/// Registers an opt-in ambient documentation export command.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <param name="configure">Optional export settings.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseDocumentationExport(
		this ReplApp app,
		Action<DocumentationExportOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseDocumentationExport(configure);
		return app;
	}
}
