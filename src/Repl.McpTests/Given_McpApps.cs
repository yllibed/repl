using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpApps
{
	[TestMethod]
	[Description("EnableApps advertises the MCP Apps UI extension capability.")]
	public void When_AppsEnabled_Then_ServerCapabilitiesIncludeUiExtension()
	{
		var app = ReplApp.Create();
		app.Map("dashboard", () => "open dashboard").ReadOnly();

		var options = app.BuildMcpServerOptions(o => o.EnableApps = true);

#pragma warning disable MCPEXP001
		options.Capabilities!.Extensions.Should().ContainKey(McpAppMetadata.ExtensionName);
#pragma warning restore MCPEXP001
	}

	[TestMethod]
	[Description("WithMcpApp adds UI metadata to the MCP tool declaration.")]
	public void When_CommandHasMcpApp_Then_ToolContainsUiMetadata()
	{
		var app = ReplApp.Create();
		app.Map("dashboard", () => "open dashboard")
			.ReadOnly()
			.WithMcpApp("ui://contacts/dashboard", McpAppVisibility.ModelAndApp);

		var options = app.BuildMcpServerOptions(o => o.EnableApps = true);
		var tool = options.ToolCollection!.Single(tool =>
			string.Equals(tool.ProtocolTool.Name, "dashboard", StringComparison.Ordinal));
		var ui = tool.ProtocolTool.Meta!["ui"]!.AsObject();

		ui["resourceUri"]!.GetValue<string>().Should().Be("ui://contacts/dashboard");
		ui["visibility"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().BeEquivalentTo(["model", "app"]);
	}

	[TestMethod]
	[Description("UiResource returns an MCP App HTML resource with CSP metadata.")]
	public async Task When_UiResourceRead_Then_ReturnsHtmlWithMcpAppMimeType()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("dashboard", () => "open dashboard")
					.ReadOnly()
					.WithMcpApp("ui://contacts/dashboard");
			},
			options => options.UiResource(
				"ui://contacts/dashboard",
				"<!doctype html><html><body>Dashboard</body></html>",
				resource =>
				{
					resource.Name = "Contacts Dashboard";
					resource.Description = "Interactive contacts dashboard";
					resource.Csp = new McpAppCsp
					{
						ConnectDomains = ["https://api.example.com"],
						ResourceDomains = ["https://cdn.example.com"],
					};
					resource.PrefersBorder = true;
				})).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();
		var csp = ui["csp"]!.AsObject();

		content.MimeType.Should().Be(McpAppValidation.ResourceMimeType);
		content.Text.Should().Contain("Dashboard");
		ui["prefersBorder"]!.GetValue<bool>().Should().BeTrue();
		csp["connectDomains"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().ContainSingle("https://api.example.com");
		csp["resourceDomains"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().ContainSingle("https://cdn.example.com");
	}

	[TestMethod]
	[Description("Apps metadata does not change regular tool fallback output.")]
	public async Task When_AppToolCalled_Then_TextFallbackStillWorks()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("dashboard", () => "Open the contacts dashboard.")
					.ReadOnly()
					.WithMcpApp("ui://contacts/dashboard");
			},
			options => options.UiResource(
				"ui://contacts/dashboard",
				"<!doctype html><html><body>Dashboard</body></html>")).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync("dashboard").ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		result.Content.OfType<TextContentBlock>().Single().Text
			.Should().Contain("Open the contacts dashboard.");
	}

	[TestMethod]
	[Description("AsMcpAppResource maps a DI-backed command as an MCP App HTML resource.")]
	public async Task When_CommandIsMcpAppResource_Then_ResourceReadUsesInjectedServices()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("contacts dashboard", (DashboardService service) =>
						$"<!doctype html><html><body>{service.Title}</body></html>")
					.WithDescription("Open dashboard")
					.AsMcpAppResource(resource =>
					{
						resource.Name = "Contacts Dashboard";
						resource.PrefersBorder = true;
					});
			},
			configureServices: services =>
			{
				services.AddSingleton(new DashboardService("Injected contacts"));
			}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();

		content.MimeType.Should().Be(McpAppValidation.ResourceMimeType);
		content.Text.Should().Contain("Injected contacts");
		ui["prefersBorder"]!.GetValue<bool>().Should().BeTrue();
	}

	[TestMethod]
	[Description("AsMcpAppResource can mark an HTML-producing command as app-only.")]
	public void When_CommandIsAppOnlyMcpAppResource_Then_ToolVisibilityIsApp()
	{
		var app = ReplApp.Create();
		app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
			.AsMcpAppResource(visibility: McpAppVisibility.App);

		var options = app.BuildMcpServerOptions();
		var tool = options.ToolCollection!.Single(tool =>
			string.Equals(tool.ProtocolTool.Name, "contacts_dashboard", StringComparison.Ordinal));
		var ui = tool.ProtocolTool.Meta!["ui"]!.AsObject();

		ui["resourceUri"]!.GetValue<string>().Should().Be("ui://contacts/dashboard");
		ui["visibility"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().ContainSingle("app");
	}

	[TestMethod]
	[Description("AsMcpAppResource can add preferred display metadata for hosts that support it.")]
	public async Task When_CommandHasPreferredDisplayMode_Then_ResourceMetaContainsDisplayPreference()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource(
					visibility: McpAppVisibility.App,
					preferredDisplayMode: McpAppDisplayModes.Fullscreen);
		}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();

		ui["preferredDisplayMode"]!.GetValue<string>().Should().Be(McpAppDisplayModes.Fullscreen);
	}

	[TestMethod]
	[Description("MCP App resource options can include host-specific UI metadata.")]
	public async Task When_CommandHasCustomUiMetadata_Then_ResourceMetaIncludesIt()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource(resource =>
				{
					resource.UiMetadata["presentation"] = "flyout";
				});
		}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();

		ui["presentation"]!.GetValue<string>().Should().Be("flyout");
	}

	[TestMethod]
	[Description("A model-visible launcher tool can point at an app-only HTML resource command.")]
	public async Task When_ModelLauncherUsesAppOnlyResource_Then_ModelToolDoesNotReturnHtml()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "Opening the contacts dashboard.")
				.ReadOnly()
				.WithMcpApp("ui://contacts/dashboard");

			app.Map("contacts dashboard app", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource("ui://contacts/dashboard", visibility: McpAppVisibility.App);
		}).ConfigureAwait(false);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var launcher = tools.Single(tool =>
			string.Equals(tool.Name, "contacts_dashboard", StringComparison.Ordinal));
		var appOnly = tools.Single(tool =>
			string.Equals(tool.Name, "contacts_dashboard_app", StringComparison.Ordinal));
		var launcherUi = launcher.ProtocolTool.Meta!["ui"]!.AsObject();
		var appOnlyUi = appOnly.ProtocolTool.Meta!["ui"]!.AsObject();

		launcherUi["visibility"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().BeEquivalentTo(["model", "app"]);
		appOnlyUi["visibility"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().ContainSingle("app");

		var toolResult = await fixture.Client.CallToolAsync("contacts_dashboard").ConfigureAwait(false);
		toolResult.Content.OfType<TextContentBlock>().Single().Text
			.Should().Contain("Opening the contacts dashboard.");

		var resourceResult = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		resourceResult.Contents.OfType<TextResourceContents>().Single().Text
			.Should().Contain("Contacts");
	}

	[TestMethod]
	[Description("AsMcpAppResource generates ui:// URI templates from route paths.")]
	public async Task When_CommandIsParameterizedMcpAppResource_Then_UiUriTemplateBindsRouteArguments()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contact {id:int} panel", (int id) =>
					$"<!doctype html><html><body>Contact {id}</body></html>")
				.WithDescription("Open contact panel")
				.AsMcpAppResource();
		}).ConfigureAwait(false);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(tool =>
			string.Equals(tool.Name, "contact_panel", StringComparison.Ordinal));
		var ui = tool.ProtocolTool.Meta!["ui"]!.AsObject();
		var result = await fixture.Client.ReadResourceAsync("ui://contact/42/panel").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();

		ui["resourceUri"]!.GetValue<string>().Should().Be("ui://contact/{id}/panel");
		content.Text.Should().Contain("Contact 42");
	}

	[TestMethod]
	[Description("AsMcpAppResource includes nested context paths when it generates ui:// URI templates.")]
	public async Task When_CommandIsNestedMcpAppResource_Then_UiUriTemplateIncludesContexts()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Context("viewer", viewer =>
			{
				viewer.Context("session {id:int}", session =>
				{
					session.Map("attach", (int id) =>
							$"<!doctype html><html><body>Session {id}</body></html>")
						.AsMcpAppResource();
				});
			});
		}).ConfigureAwait(false);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(tool =>
			string.Equals(tool.Name, "viewer_session_attach", StringComparison.Ordinal));
		var ui = tool.ProtocolTool.Meta!["ui"]!.AsObject();
		var result = await fixture.Client.ReadResourceAsync("ui://viewer/session/42/attach").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();

		ui["resourceUri"]!.GetValue<string>().Should().Be("ui://viewer/session/{id}/attach");
		content.Text.Should().Contain("Session 42");
	}

	[TestMethod]
	[Description("AsMcpAppResource supports custom route constraints when it generates ui:// URI templates.")]
	public async Task When_CommandUsesCustomConstraint_Then_UiUriTemplateBindsRouteArgument()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options => options.Parsing.AddRouteConstraint(
				"tenant-slug",
				static value => value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-')));

			app.Map("tenant {slug:tenant-slug} panel", (string slug) =>
					$"<!doctype html><html><body>Tenant {slug}</body></html>")
				.AsMcpAppResource();
		}).ConfigureAwait(false);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(tool =>
			string.Equals(tool.Name, "tenant_panel", StringComparison.Ordinal));
		var ui = tool.ProtocolTool.Meta!["ui"]!.AsObject();
		var result = await fixture.Client.ReadResourceAsync("ui://tenant/acme-prod/panel").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();

		ui["resourceUri"]!.GetValue<string>().Should().Be("ui://tenant/{slug}/panel");
		content.Text.Should().Contain("Tenant acme-prod");
	}

	private sealed record DashboardService(string Title);
}
