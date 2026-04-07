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
	/// <param name="visibility">Whether the linked tool is visible to the model, the app iframe, or both.</param>
	/// <param name="preferredDisplayMode">Optional preferred display mode. Hosts decide whether they support it.</param>
	/// <returns>The same builder instance.</returns>
	public static CommandBuilder AsMcpAppResource(
		this CommandBuilder builder,
		McpAppVisibility visibility = McpAppVisibility.ModelAndApp,
		string? preferredDisplayMode = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		var resourceUri = McpToolNameFlattener.BuildResourceUri(builder.Route, "ui");
		return builder.AsMcpAppResource(resourceUri, visibility, preferredDisplayMode);
	}

	/// <summary>
	/// Marks this command as an MCP App UI resource and links the command's tool declaration to it.
	/// The handler return value should be a complete HTML document.
	/// </summary>
	/// <param name="builder">Command builder.</param>
	/// <param name="resourceUri">The <c>ui://</c> resource URI.</param>
	/// <param name="visibility">Whether the linked tool is visible to the model, the app iframe, or both.</param>
	/// <param name="preferredDisplayMode">Optional preferred display mode. Hosts decide whether they support it.</param>
	/// <returns>The same builder instance.</returns>
	public static CommandBuilder AsMcpAppResource(
		this CommandBuilder builder,
		string resourceUri,
		McpAppVisibility visibility = McpAppVisibility.ModelAndApp,
		string? preferredDisplayMode = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		McpAppValidation.ThrowIfInvalidUiUri(resourceUri);

		var options = new McpAppResourceOptions();
		options.PreferredDisplayMode ??= preferredDisplayMode;

		builder
			.ReadOnly()
			.AsResource()
			.WithMetadata(
				McpAppMetadata.ResourceMetadataKey,
				new McpAppCommandResourceOptions(resourceUri, options, visibility));

		return builder;
	}

	/// <summary>
	/// Sets the fallback text returned when the MCP App launcher tool is called.
	/// </summary>
	public static CommandBuilder WithMcpAppLauncherText(this CommandBuilder builder, string text)
	{
		GetResourceOptions(builder).LauncherText = string.IsNullOrWhiteSpace(text)
			? throw new ArgumentException("Launcher text cannot be empty.", nameof(text))
			: text;
		return builder;
	}

	/// <summary>
	/// Sets the visual boundary preference for the MCP App.
	/// </summary>
	public static CommandBuilder WithMcpAppBorder(this CommandBuilder builder, bool prefersBorder = true)
	{
		GetResourceOptions(builder).PrefersBorder = prefersBorder;
		return builder;
	}

	/// <summary>
	/// Sets the preferred display mode for hosts that support display mode changes.
	/// </summary>
	public static CommandBuilder WithMcpAppDisplayMode(this CommandBuilder builder, string displayMode)
	{
		GetResourceOptions(builder).PreferredDisplayMode = string.IsNullOrWhiteSpace(displayMode)
			? throw new ArgumentException("Display mode cannot be empty.", nameof(displayMode))
			: displayMode;
		return builder;
	}

	/// <summary>
	/// Sets the Content Security Policy metadata for the MCP App.
	/// </summary>
	public static CommandBuilder WithMcpAppCsp(this CommandBuilder builder, McpAppCsp csp)
	{
		ArgumentNullException.ThrowIfNull(csp);
		GetResourceOptions(builder).Csp = csp;
		return builder;
	}

	/// <summary>
	/// Sets a host-specific dedicated domain hint for the MCP App.
	/// </summary>
	public static CommandBuilder WithMcpAppDomain(this CommandBuilder builder, string domain)
	{
		GetResourceOptions(builder).Domain = string.IsNullOrWhiteSpace(domain)
			? throw new ArgumentException("Domain cannot be empty.", nameof(domain))
			: domain;
		return builder;
	}

	/// <summary>
	/// Sets browser permission metadata for the MCP App.
	/// </summary>
	public static CommandBuilder WithMcpAppPermissions(
		this CommandBuilder builder,
		McpAppPermissions permissions)
	{
		ArgumentNullException.ThrowIfNull(permissions);
		GetResourceOptions(builder).Permissions = permissions;
		return builder;
	}

	/// <summary>
	/// Adds a host-specific UI metadata value.
	/// </summary>
	public static CommandBuilder WithMcpAppUiMetadata(
		this CommandBuilder builder,
		string key,
		string value)
	{
		key = string.IsNullOrWhiteSpace(key)
			? throw new ArgumentException("Metadata key cannot be empty.", nameof(key))
			: key;
		value = string.IsNullOrWhiteSpace(value)
			? throw new ArgumentException("Metadata value cannot be empty.", nameof(value))
			: value;
		GetResourceOptions(builder).UiMetadata[key] = value;
		return builder;
	}

	private static McpAppResourceOptions GetResourceOptions(CommandBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);
		if (builder.Metadata.TryGetValue(McpAppMetadata.ResourceMetadataKey, out var value)
			&& value is McpAppCommandResourceOptions options)
		{
			return options.ResourceOptions;
		}

		throw new InvalidOperationException("Call AsMcpAppResource() before configuring MCP App metadata.");
	}
}
