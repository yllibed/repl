namespace Repl.Mcp;

/// <summary>
/// Extension methods for linking Repl commands to MCP App UI resources.
/// </summary>
public static class McpAppCommandBuilderExtensions
{
	/// <summary>
	/// Links a command to an MCP App UI resource.
	/// </summary>
	/// <param name="builder">Command builder.</param>
	/// <param name="resourceUri">The <c>ui://</c> resource rendered for this command.</param>
	/// <param name="visibility">Whether the tool is visible to the model, the app iframe, or both.</param>
	/// <returns>The same builder instance.</returns>
	public static CommandBuilder WithMcpApp(
		this CommandBuilder builder,
		string resourceUri,
		McpAppVisibility visibility = McpAppVisibility.ModelAndApp)
	{
		ArgumentNullException.ThrowIfNull(builder);
		McpAppValidation.ThrowIfInvalidUiUri(resourceUri);
		return builder.WithMetadata(
			McpAppMetadata.CommandMetadataKey,
			new McpAppToolOptions(resourceUri) { Visibility = visibility });
	}

	/// <summary>
	/// Marks this command as an MCP App UI resource and links the command's tool declaration to it.
	/// The <c>ui://</c> resource URI is generated from the command route.
	/// The handler return value should be a complete HTML document.
	/// </summary>
	/// <param name="builder">Command builder.</param>
	/// <param name="configure">Optional resource metadata configuration.</param>
	/// <param name="visibility">Whether the linked tool is visible to the model, the app iframe, or both.</param>
	/// <param name="preferredDisplayMode">Optional preferred display mode. Hosts decide whether they support it.</param>
	/// <returns>The same builder instance.</returns>
	public static CommandBuilder AsMcpAppResource(
		this CommandBuilder builder,
		Action<McpAppResourceOptions>? configure = null,
		McpAppVisibility visibility = McpAppVisibility.ModelAndApp,
		string? preferredDisplayMode = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		var resourceUri = McpToolNameFlattener.BuildResourceUri(builder.Route, "ui");
		return builder.AsMcpAppResource(resourceUri, configure, visibility, preferredDisplayMode);
	}

	/// <summary>
	/// Marks this command as an MCP App UI resource and links the command's tool declaration to it.
	/// The handler return value should be a complete HTML document.
	/// </summary>
	/// <param name="builder">Command builder.</param>
	/// <param name="resourceUri">The <c>ui://</c> resource URI.</param>
	/// <param name="configure">Optional resource metadata configuration.</param>
	/// <param name="visibility">Whether the linked tool is visible to the model, the app iframe, or both.</param>
	/// <param name="preferredDisplayMode">Optional preferred display mode. Hosts decide whether they support it.</param>
	/// <returns>The same builder instance.</returns>
	public static CommandBuilder AsMcpAppResource(
		this CommandBuilder builder,
		string resourceUri,
		Action<McpAppResourceOptions>? configure = null,
		McpAppVisibility visibility = McpAppVisibility.ModelAndApp,
		string? preferredDisplayMode = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		McpAppValidation.ThrowIfInvalidUiUri(resourceUri);

		var options = new McpAppResourceOptions();
		configure?.Invoke(options);
		options.Name ??= resourceUri;
		options.PreferredDisplayMode ??= preferredDisplayMode;

		builder
			.ReadOnly()
			.AsResource()
			.WithMcpApp(resourceUri, visibility)
			.WithMetadata(
				McpAppMetadata.ResourceMetadataKey,
				new McpAppCommandResourceOptions(resourceUri, options));

		return builder;
	}
}
