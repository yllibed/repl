namespace Repl;

public sealed partial class CoreReplApp
{
	private DocumentationEngine? _documentationEngine;
	private DocumentationEngine DocumentationEng => _documentationEngine ??= new(this);

	/// <inheritdoc />
	public ReplDocumentationModel CreateDocumentationModel(string? targetPath = null) =>
		DocumentationEng.CreateDocumentationModel(targetPath);

	internal ReplDocumentationModel CreateDocumentationModel(
		IServiceProvider serviceProvider,
		string? targetPath = null) =>
		DocumentationEng.CreateDocumentationModel(serviceProvider, targetPath);

	/// <summary>
	/// Internal documentation model creation that supports not-found result for help rendering.
	/// </summary>
	internal object CreateDocumentationModelInternal(string? targetPath) =>
		DocumentationEng.CreateDocumentationModelInternal(targetPath);

	internal ReplDocApp BuildDocumentationApp() =>
		DocumentationEng.BuildDocumentationApp();
}
