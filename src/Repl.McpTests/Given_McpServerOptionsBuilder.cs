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
	[Description("BuildMcpServerOptions captures the current tools as a pre-populated collection.")]
	public void When_AppHasCommands_Then_OptionsContainToolCollection()
	{
		var app = ReplApp.Create();
		app.Map("greet {name}", (string name) => $"Hello, {name}!").ReadOnly();
		app.Map("ping", () => "pong");

		var options = app.BuildMcpServerOptions();

		options.ToolCollection.Should().NotBeNull();
		options.ToolCollection.Should().Contain(tool => string.Equals(tool.ProtocolTool.Name, "greet", StringComparison.Ordinal));
		options.ToolCollection.Should().Contain(tool => string.Equals(tool.ProtocolTool.Name, "ping", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("BuildMcpServerOptions captures the current resources as a pre-populated collection.")]
	public void When_AppHasReadOnlyCommands_Then_OptionsContainResourceCollection()
	{
		var app = ReplApp.Create();
		app.Map("status", () => "ok").ReadOnly();

		var options = app.BuildMcpServerOptions();

		options.ResourceCollection.Should().NotBeNull();
		options.ResourceCollection.Should().ContainSingle(resource =>
			resource.ProtocolResource != null
			&& string.Equals(resource.ProtocolResource.Name, "status", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("BuildMcpServerOptions produces options usable with McpServer.Create via pipes.")]
	public async Task When_OptionsUsedWithMcpServer_Then_ServerResponds()
	{
		var app = ReplApp.Create();
		app.Map("echo {msg}", (string msg) => $"echo:{msg}");

		var mcpOptions = app.BuildMcpServerOptions();

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
