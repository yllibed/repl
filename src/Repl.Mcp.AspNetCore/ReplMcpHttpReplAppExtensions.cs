namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Extension methods for enabling Repl MCP Streamable HTTP CLI hosting.
/// </summary>
public static class ReplMcpHttpReplAppExtensions
{
	/// <summary>
	/// Enables MCP Streamable HTTP server mode via <c>{commandName} mcp httpserve</c>.
	/// </summary>
	/// <param name="app">Target Repl app.</param>
	/// <param name="configure">Optional server configuration callback.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseMcpHttpServer(
		this ReplApp app,
		Action<ReplMcpHttpServerOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var options = new ReplMcpHttpServerOptions();
		configure?.Invoke(options);

		app.MapModule(
			new McpHttpModule(app, options),
			static context => context.Channel is ReplRuntimeChannel.Cli);

		return app;
	}
}
