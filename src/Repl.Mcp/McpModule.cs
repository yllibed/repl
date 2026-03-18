namespace Repl.Mcp;

/// <summary>
/// Registers the <c>mcp serve</c> command as a hidden protocol passthrough context.
/// Follows the same pattern as <c>ShellCompletionModule</c>.
/// </summary>
internal sealed class McpModule(ReplMcpServerOptions options) : IReplModule
{
	public void Map(IReplMap map)
	{
		map.Context(options.ContextName, mcp =>
		{
			mcp.Map("serve",
				async (IReplIoContext io, IServiceProvider services, ICoreReplApp app, CancellationToken ct) =>
				{
					var handler = new McpServerHandler(app, options, services);
					await handler.RunAsync(io, ct).ConfigureAwait(false);
					return Results.Exit(0);
				})
				.WithDescription("Start MCP stdio server for agent integration.")
				.AsProtocolPassthrough()
				.Hidden();
		})
		.Hidden();
	}
}
