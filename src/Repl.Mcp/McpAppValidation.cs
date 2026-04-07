namespace Repl.Mcp;

internal static class McpAppValidation
{
	public const string ResourceMimeType = "text/html;profile=mcp-app";
	private const string UiScheme = "ui://";

	public static void ThrowIfInvalidUiUri(string uri)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uri);
		if (!uri.StartsWith(UiScheme, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("MCP App resource URIs must use the ui:// scheme.", nameof(uri));
		}
	}
}
