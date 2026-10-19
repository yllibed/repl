using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;
using Repl.Mcp;
using Repl.Parameters;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpServerOptionsBuilder
{
	[TestMethod]
	public void When_ConfiguringSdkOptions_Then_ProjectionIsReplacedAndHostSettingsSurvive()
	{
		var original = ReplApp.Create();
		original.Map("old", () => "old");
		var target = original.BuildMcpServerOptions();
		var originalTools = target.ToolCollection;
		target.ProtocolVersion = "2026-07-28";
		target.ServerInstructions = "host instructions";
		target.ScopeRequests = false;
		var handlers = target.Handlers;
		var filters = target.Filters;
		var app = CoreReplApp.Create();
		app.Map("new", () => "new");
		app.Map("resource", () => "data").AsResource();
		app.Map("prompt", () => "instructions").AsPrompt();

		app.ConfigureMcpServerOptions(target, options => options.ServerName = "projected");

		target.ToolCollection.Should().ContainSingle().Which.ProtocolTool.Name.Should().Be("new");
		originalTools.Should().ContainSingle().Which.ProtocolTool.Name.Should().Be("old");
		target.ResourceCollection.Should().ContainSingle();
		target.PromptCollection.Should().ContainSingle();
		target.ServerInfo!.Name.Should().Be("projected");
		target.Capabilities!.Tools.Should().NotBeNull();
		target.ProtocolVersion.Should().Be("2026-07-28");
		target.ServerInstructions.Should().Be("host instructions");
		target.ScopeRequests.Should().BeFalse();
		target.Handlers.Should().BeSameAs(handlers);
		target.Filters.Should().BeSameAs(filters);
	}

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
	[Description("Core MCP dispatch guarantees a per-call interaction channel even when no base provider is supplied. Static discovery must retain a tool whose AutomationHidden required progress parameter is therefore synthesizable.")]
	public void When_CoreMcpOptionsHaveNoBaseProvider_Then_RequiredHiddenProgressToolIsRetained()
	{
		var app = CoreReplApp.Create();
		app.Map(
			"sync",
			([ReplOption(Name = "progress", Arity = ReplArity.ExactlyOne, AutomationHidden = true)] IProgress<ReplProgressEvent> progress) =>
				progress is not null ? "progress-ready" : "missing");

		var options = app.BuildMcpServerOptions();

		options.ToolCollection.Should().Contain(tool =>
			string.Equals(tool.ProtocolTool.Name, "sync", StringComparison.Ordinal));
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
