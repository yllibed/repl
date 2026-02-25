namespace Repl;

/// <summary>
/// Documentation export extensions.
/// </summary>
public static class ReplDocumentationExtensions
{
	/// <summary>
	/// Registers an opt-in ambient documentation export command.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <param name="configure">Optional export settings.</param>
	/// <returns>The same app instance.</returns>
	public static CoreReplApp UseDocumentationExport(
		this CoreReplApp app,
		Action<DocumentationExportOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new DocumentationExportOptions();
		configure?.Invoke(options);
		if (string.IsNullOrWhiteSpace(options.CommandRoute))
		{
			throw new ArgumentException("Documentation export route cannot be empty.", nameof(configure));
		}

		var builder = app.Map(
			options.CommandRoute,
			(string[]? targetPathTokens) =>
			{
				var targetPath = targetPathTokens is null or { Length: 0 }
					? null
					: string.Join(' ', targetPathTokens);
				return app.CreateDocumentationModel(targetPath);
			});
		if (options.HiddenByDefault)
		{
			builder.Hidden();
		}

		return app;
	}
}
