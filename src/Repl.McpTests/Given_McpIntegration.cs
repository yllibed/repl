using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpIntegration
{
	[TestMethod]
	[Description("UseMcpServer registers the 'mcp serve' route on the app.")]
	public void When_UseMcpServer_Then_McpServeRouteIsRegistered()
	{
		var app = ReplApp.Create();
		app.Map("hello", () => "world");
		app.UseMcpServer();

		// Resolve through the internal CoreReplApp to verify routing.
		var match = app.Core.Resolve(["mcp", "serve"]);

		match.Should().NotBeNull();
		match!.Route.Command.IsProtocolPassthrough.Should().BeTrue();
	}

	[TestMethod]
	[Description("UseMcpServer with custom command name uses that name.")]
	public void When_CustomContextName_Then_RouteUsesCustomName()
	{
		var app = ReplApp.Create();
		app.Map("hello", () => "world");
		app.UseMcpServer(o => o.ContextName = "ai");

		var match = app.Core.Resolve(["ai", "serve"]);

		match.Should().NotBeNull();
	}

	[TestMethod]
	[Description("MCP serve route is hidden from discovery.")]
	public void When_UseMcpServer_Then_McpContextIsHidden()
	{
		var app = ReplApp.Create();
		app.Map("hello", () => "world");
		app.UseMcpServer();

		var model = app.CreateDocumentationModel();

		model.Commands.Should().NotContain(c => string.Equals(c.Path, "mcp serve", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("MCP server options advertise logging capability because interaction feedback can be routed through MCP notifications.")]
	public void When_BuildingMcpOptions_Then_LoggingCapabilityIsAdvertised()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();

		var options = app.BuildMcpServerOptions();

		options.Capabilities!.Logging.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Commands marked AutomationHidden are excluded from MCP tool candidates.")]
	public void When_CommandIsAutomationHidden_Then_ExcludedFromToolCandidates()
	{
		var app = ReplApp.Create();
		app.Map("list", () => "ok").ReadOnly();
		app.Map("wizard", () => "ok").AutomationHidden();

		var model = app.CreateDocumentationModel();
		var toolCandidates = model.Commands.Where(c =>
			!c.IsHidden && c.Annotations?.AutomationHidden != true).ToList();

		toolCandidates.Should().ContainSingle(c => string.Equals(c.Path, "list", StringComparison.Ordinal));
		toolCandidates.Should().NotContain(c => string.Equals(c.Path, "wizard", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Documentation model includes enriched metadata for MCP commands.")]
	public void When_EnrichedCommands_Then_DocModelContainsAllFields()
	{
		var app = ReplApp.Create();
		app.Map("deploy {env}", (string env) => $"Deployed to {env}")
			.WithDescription("Deploy application")
			.WithDetails("Deploys to the specified environment.")
			.Destructive()
			.OpenWorld()
			.LongRunning();
		app.UseMcpServer();

		var model = app.CreateDocumentationModel();
		var cmd = model.Commands.Single(c => string.Equals(c.Path, "deploy {env}", StringComparison.Ordinal));

		cmd.Description.Should().Be("Deploy application");
		cmd.Details.Should().Be("Deploys to the specified environment.");
		cmd.Annotations!.Destructive.Should().BeTrue();
		cmd.Annotations!.OpenWorld.Should().BeTrue();
		cmd.Annotations!.LongRunning.Should().BeTrue();
		cmd.Arguments.Should().ContainSingle(a => string.Equals(a.Name, "env", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Modules excluded from Programmatic channel are not visible as MCP tools.")]
	public void When_ModuleExcludedFromProgrammatic_Then_NotInToolCandidates()
	{
		var app = ReplApp.Create();
		app.Map("public-cmd", () => "ok").ReadOnly();
		app.MapModule(
			new AdminModule(),
			ctx => ctx.Channel != ReplRuntimeChannel.Programmatic);
		app.UseMcpServer();

		// Simulate programmatic channel by setting the flag.
		ReplSessionIO.IsProgrammatic = true;
		try
		{
			var model = app.Core.CreateDocumentationModel();
			model.Commands.Should().NotContain(c => string.Equals(c.Path, "admin reset", StringComparison.Ordinal));
			model.Commands.Should().Contain(c => string.Equals(c.Path, "public-cmd", StringComparison.Ordinal));
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = false;
		}
	}

	private sealed class AdminModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Map("admin reset", () => "reset done");
		}
	}
}
