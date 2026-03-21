using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpServerOptionsBuilder
{
	[TestMethod]
	[Description("BuildMcpServerOptions wires the dynamic tools handler from the app's command graph.")]
	public void When_AppHasCommands_Then_OptionsContainListToolsHandler()
	{
		var app = ReplApp.Create();
		app.Map("greet {name}", (string name) => $"Hello, {name}!").ReadOnly();
		app.Map("ping", () => "pong");

		var options = app.Core.BuildMcpServerOptions();

		options.Handlers.ListToolsHandler.Should().NotBeNull();
		options.Handlers.CallToolHandler.Should().NotBeNull();
	}

	[TestMethod]
	[Description("BuildMcpServerOptions wires the dynamic resources handler from ReadOnly commands.")]
	public void When_AppHasReadOnlyCommands_Then_OptionsContainResourcesHandler()
	{
		var app = ReplApp.Create();
		app.Map("status", () => "ok").ReadOnly();

		var options = app.Core.BuildMcpServerOptions();

		options.Handlers.ListResourcesHandler.Should().NotBeNull();
		options.Handlers.ReadResourceHandler.Should().NotBeNull();
	}

	[TestMethod]
	[Description("BuildMcpServerOptions produces options usable with McpServer.Create via pipes.")]
	public async Task When_OptionsUsedWithMcpServer_Then_ServerResponds()
	{
		var app = ReplApp.Create();
		app.Map("echo {msg}", (string msg) => $"echo:{msg}");

		var mcpOptions = app.Core.BuildMcpServerOptions();

		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		using var cts = new CancellationTokenSource();

		var transport = new StreamServerTransport(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream(),
			"test-server");
		var server = McpServer.Create(transport, mcpOptions);
		var serverTask = server.RunAsync(cts.Token);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());
		var client = await McpClient.CreateAsync(clientTransport).ConfigureAwait(false);

		await using (client.ConfigureAwait(false))
		{
			var tools = await client.ListToolsAsync().ConfigureAwait(false);
			tools.Should().ContainSingle(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));
		}

		await cts.CancelAsync().ConfigureAwait(false);
		await clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
		await serverToClient.Writer.CompleteAsync().ConfigureAwait(false);

		try { await serverTask.ConfigureAwait(false); }
		catch (OperationCanceledException) { /* Expected: server shutdown. */ }

		await server.DisposeAsync().ConfigureAwait(false);
	}
}
