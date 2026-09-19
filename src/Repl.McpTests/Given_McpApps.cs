using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Interaction;
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
	[Description("The MCP Apps capability probe inspects route metadata without re-invoking CommandFilter; the predicate runs once per command in the generated snapshot.")]
	public void When_CommandFilterIsConfigured_Then_StaticAppsCapabilityProbeDoesNotReinvokeIt()
	{
		var app = ReplApp.Create();
		app.Map("dashboard", () => "open dashboard")
			.ReadOnly()
			.WithMcpApp("ui://contacts/dashboard", McpAppVisibility.ModelAndApp);
		var filterCalls = 0;

		var options = app.BuildMcpServerOptions(o => o.CommandFilter = _ =>
		{
			filterCalls++;
			return true;
		});

		filterCalls.Should().Be(1);
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
	[Description("Regression guard: a UI resource read that fails must carry the feedback its command emitted. On success the trailing blocks are dropped on purpose — the body has to match the advertised MIME type — but a failure has no body at all, so the surfaced error is the only place left for them, and reading just the first content block discarded them.")]
	public async Task When_AFailingUiResourceReadEmitsFeedback_Then_ItRidesInTheError()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map(
				"dashboard",
				static async Task<string> (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
				{
					await interaction.WriteNoticeAsync(
						text: "render-notice",
						cancellationToken: cancellationToken).ConfigureAwait(false);
					throw new InvalidOperationException("render-failed");
				}).AsMcpAppResource("ui://contacts/dashboard");
		}).ConfigureAwait(false);

		var act = async () =>
			await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);

		(await act.Should().ThrowAsync<Exception>().ConfigureAwait(false))
			.Which.Message.Should().Contain(
				"render-notice",
				because: "a failed read has nowhere else to carry what the command reported");
	}

	[TestMethod]
	[Description("Regression guard: a raw UI resource handler reading IMcpClientRoots.Current must see the connection\u0027s roots on the first read. This resource is the one prebuilt primitive that does not run through McpToolAdapter, so it does not inherit the execution-boundary priming the command-backed paths get and needs its own.")]
	public async Task When_ARawUiResourceReadsCurrentRoots_Then_TheFirstReadSeesThem()
	{
		// Roots are deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
		// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		var clientOptions = new McpClientOptions
		{
			Capabilities = new ClientCapabilities
			{
				Roots = new RootsCapability { ListChanged = true },
			},
			Handlers = new McpClientHandlers
			{
				RootsHandler = static (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = "file:///C:/workspace", Name = "workspace" }],
				}),
			},
		};
#pragma warning restore MCP9005

		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/roots",
				(IMcpClientRoots roots) =>
					"<!doctype html><html><body>"
					+ string.Join(',', roots.Current.Select(static root => root.Uri.ToString()))
					+ "</body></html>"),
			clientOptions: clientOptions).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://probe/roots").ConfigureAwait(false);

		result.Contents.OfType<TextResourceContents>().Single().Text.Should().Contain(
			"file:///C:/workspace",
			because: "Current is documented as the connection\u0027s effective roots on every execution path");
	}

	[TestMethod]
	[Description("Regression guard: a raw UI resource handler that reports feedback and then throws must not lose it. On 2026-07-28 a request that declared no log level receives no message notifications, so the buffer is the only carrier — and this primitive opened none, because it bypasses the adapter that opens one for every other path.")]
	public async Task When_ARawUiResourceReportsThenFails_Then_TheFeedbackRidesInTheError()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/fails",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"render-warning",
						cancellationToken).ConfigureAwait(false);
					throw new InvalidOperationException("render-failed");
				})).ConfigureAwait(false);

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var act = async () => await fixture.Client.ReadResourceAsync("ui://probe/fails").ConfigureAwait(false);

		(await act.Should().ThrowAsync<Exception>().ConfigureAwait(false))
			.Which.Message.Should().Contain(
				"render-warning",
				because: "a failed read has no body, so the error is the only place the warning can ride");
	}

	[TestMethod]
	[Description("Pins the other half of the feedback rule: a UI resource read that SUCCEEDS keeps only its body. Every other path appends an undeliverable message to the result, and a resource read cannot \u2014 its result is a typed body whose MIME type was advertised, so appending prose would corrupt what the client parses. Only the failing read, which has no body left to protect, carries the feedback in its error.")]
	public async Task When_ARawUiResourceReportsAndSucceeds_Then_TheBodyCarriesNoFeedback()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/reports",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"render-warning",
						cancellationToken).ConfigureAwait(false);
					return "<!doctype html><html><body>ok</body></html>";
				})).ConfigureAwait(false);

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var read = await fixture.Client.ReadResourceAsync("ui://probe/reports").ConfigureAwait(false);

		var body = read.Contents.OfType<TextResourceContents>().Single();
		body.MimeType.Should().Be(McpAppValidation.ResourceMimeType);
		body.Text.Should().Be(
			"<!doctype html><html><body>ok</body></html>",
			because: "the advertised MIME type is a promise about the body, and feedback is not part of it");
	}

	[TestMethod]
	[Description("Regression guard: a UI resource handler that runs its own budget, reports feedback and then gives up must not lose that feedback. Cancellation is told apart by who asked for it, not by the exception type \u2014 the caller abandoning the request is the only case with nobody left to read the answer. A handler timing out internally is a failure like any other, and its diagnostic is the only thing explaining why the read produced nothing.")]
	public async Task When_ARawUiResourceReportsThenCancelsItself_Then_TheFeedbackStillRides()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/self-cancels",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"render-warning",
						cancellationToken).ConfigureAwait(false);

					// The handler's own budget, not the caller's: the request token stays live throughout.
					using var ownBudget = new CancellationTokenSource();
					await ownBudget.CancelAsync().ConfigureAwait(false);
					ownBudget.Token.ThrowIfCancellationRequested();
					return "unreachable";
				})).ConfigureAwait(false);

		var act = async () => await fixture.Client.ReadResourceAsync("ui://probe/self-cancels").ConfigureAwait(false);

		(await act.Should().ThrowAsync<Exception>().ConfigureAwait(false))
			.Which.Message.Should().Contain(
				"render-warning",
				because: "the caller never withdrew, so there is still someone to read what the handler said");
	}

	[TestMethod]
	[Description("Regression guard: a command-backed App resource that fails must carry the app-authored feedback and withhold the handler's own exception text — the same split the raw UiResource path applies. This path reaches it differently: it surfaces the tool result's blocks rather than wrapping the exception, so the two could drift apart without either being obviously wrong.")]
	public async Task When_ACommandBackedAppFails_Then_FeedbackRidesAndTheDetailIsWithheld()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("dash", async (IMcpFeedback feedback, CancellationToken ct) =>
				{
					await feedback.SendMessageAsync(McpMessageLevel.Warning, "render-notice", ct).ConfigureAwait(false);
					throw new InvalidOperationException("secret-internal-detail");
				})
				.ReadOnly()
				.AsMcpAppResource("ui://probe/dash")).ConfigureAwait(false);

		var act = async () => await fixture.Client.ReadResourceAsync("ui://probe/dash").ConfigureAwait(false);

		var message = (await act.Should().ThrowAsync<Exception>().ConfigureAwait(false)).Which.Message;

		message.Should().Contain(
			"render-notice",
			because: "the app authored that for the client and a failed read has no body to carry it");
		message.Should().NotContain(
			"secret-internal-detail",
			because: "the handler threw it, so it is the framework speaking about code, not the app speaking to a caller");
	}

	[TestMethod]
	[Description("Regression guard: an McpException a handler raises on purpose carries a message written for the client, and buffering feedback beforehand must not replace it. Withholding applies to what the framework rendered from an exception nobody meant to surface \u2014 the SDK passes an McpException through verbatim precisely because raising one is a deliberate act.")]
	public async Task When_ARawUiResourceReportsThenRaisesAnMcpException_Then_ItsMessageSurvives()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/explains",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"render-warning",
						cancellationToken).ConfigureAwait(false);
					throw new McpException("Dashboard needs a workspace root to render.");
				})).ConfigureAwait(false);

		var act = async () => await fixture.Client.ReadResourceAsync("ui://probe/explains").ConfigureAwait(false);

		var message = (await act.Should().ThrowAsync<Exception>().ConfigureAwait(false)).Which.Message;

		message.Should().Contain(
			"Dashboard needs a workspace root to render.",
			because: "the handler raised that deliberately and the client is who it was written for");
		message.Should().Contain(
			"render-warning",
			because: "buffered feedback still rides along, it does not replace the failure");
	}

	[TestMethod]
	[Description("Regression guard: a failed UI resource read must not hand the client the handler\u0027s own exception message. The SDK flattens any non-McpException to a generic string and passes an McpException\u0027s message through verbatim, so wrapping the failure in order to carry buffered feedback is precisely the act that would disclose an IOException\u0027s path or a binding failure\u0027s parameter type. Only the app-authored feedback travels.")]
	public async Task When_ARawUiResourceReportsThenFails_Then_TheHandlersOwnMessageIsWithheld()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.UiResource(
				"ui://probe/fails",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"render-warning",
						cancellationToken).ConfigureAwait(false);
					throw new InvalidOperationException("secret-internal-detail");
				})).ConfigureAwait(false);

		var act = async () => await fixture.Client.ReadResourceAsync("ui://probe/fails").ConfigureAwait(false);

		var message = (await act.Should().ThrowAsync<Exception>().ConfigureAwait(false)).Which.Message;

		message.Should().Contain(
			"render-warning",
			because: "the app authored that message for the client and a failed read has no body to carry it");
		message.Should().NotContain(
			"secret-internal-detail",
			because: "the handler\u0027s own failure text is not the app speaking and must not reach the client");
	}

	[TestMethod]
	[Description("Regression guard: the Apps extension must be advertised when the only App-bearing registration is shadowed by a later one for the same template. Route resolution keeps the last registration per template, so a probe that resolves before answering sees only the shadowing route \u2014 while every connection whose gate excludes the shadowing module is served the App underneath it, and would be handed App metadata with nothing to interpret it.")]
	public async Task When_AnAppRegistrationIsShadowed_Then_TheAppsExtensionIsStillAdvertised()
	{
		// Roots are deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
		// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.MapModule(new ShadowedAppModule());
			app.MapModule(new ShadowingPlainModule(), (IMcpClientRoots roots) => roots.IsSupported);
		}).ConfigureAwait(false);
#pragma warning restore MCP9005

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		tools.Should().Contain(
			tool => string.Equals(tool.Name, "dashboard", StringComparison.Ordinal),
			because: "this client declares no roots, so the shadowing module is absent and the App is served");

#pragma warning disable MCPEXP001
		fixture.Client.ServerCapabilities?.Extensions.Should().NotBeNull()
			.And.ContainKey(
				McpAppMetadata.ExtensionName,
				because: "the served catalog contains an App, whatever a fully-resolved probe would have kept");
#pragma warning restore MCPEXP001
	}

	private sealed class ShadowedAppModule : IReplModule
	{
		public void Map(IReplMap app) =>
			app.Map("dashboard", () => "<!doctype html><html><body>app</body></html>")
				.ReadOnly()
				.AsMcpAppResource("ui://shadowed/dashboard");
	}

	/// <summary>Registered after <see cref="ShadowedAppModule"/> and claiming the same template.</summary>
	private sealed class ShadowingPlainModule : IReplModule
	{
		public void Map(IReplMap app) => app.Map("dashboard", () => "plain").ReadOnly();
	}

	[TestMethod]
	[Description("Regression guard: the Apps extension must be advertised for any catalog the handler can build, including one whose App sits behind a gate on the roots DATA. A real initialize-era snapshot resolves the client\u0027s roots before evaluating presence predicates, so that App is in the catalog — while a requestless probe evaluates the gate as false and would advertise nothing, leaving an Apps-aware client holding App metadata it cannot interpret.")]
	public async Task When_AnAppIsGatedOnTheRootsData_Then_TheAppsExtensionIsStillAdvertised()
	{
		// Roots are deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
		// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		var clientOptions = new McpClientOptions
		{
			ProtocolVersion = McpProtocolRevisions.LastWithSessions,
			Capabilities = new ClientCapabilities
			{
				Roots = new RootsCapability { ListChanged = true },
			},
			Handlers = new McpClientHandlers
			{
				RootsHandler = static (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = "file:///C:/workspace", Name = "workspace" }],
				}),
			},
		};
#pragma warning restore MCP9005

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new DataGatedAppModule(), (IMcpClientRoots roots) => roots.Current.Count > 0),
			configureOptions: null,
			clientOptions: clientOptions).ConfigureAwait(false);

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		tools.Should().Contain(
			tool => string.Equals(tool.Name, "dashboard", StringComparison.Ordinal),
			because: "the real legacy snapshot resolves roots first, so the gate matches and the App is served");

#pragma warning disable MCPEXP001
		fixture.Client.ServerCapabilities?.Extensions.Should().NotBeNull()
			.And.ContainKey(
				McpAppMetadata.ExtensionName,
				because: "a client served App metadata must be told the extension it needs to read it");
#pragma warning restore MCPEXP001
	}

	private sealed class DataGatedAppModule : IReplModule
	{
		public void Map(IReplMap app) =>
			app.Map("dashboard", () => "<!doctype html><html><body>ok</body></html>")
				.ReadOnly()
				.AsMcpAppResource("ui://gated/dashboard");
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
					.AsMcpAppResource()
					.WithMcpAppBorder();
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
	[Description("WithMcpAppDisplayMode can add preferred display metadata for hosts that support it.")]
	public async Task When_CommandHasPreferredDisplayMode_Then_ResourceMetaContainsDisplayPreference()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource(visibility: McpAppVisibility.App)
				.WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen);
		}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();

		ui["preferredDisplayMode"]!.GetValue<string>().Should().Be(McpAppDisplayModes.Fullscreen);
	}

	[TestMethod]
	[Description("WithMcpAppUiMetadata can include host-specific UI metadata.")]
	public async Task When_CommandHasCustomUiMetadata_Then_ResourceMetaIncludesIt()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource()
				.WithMcpAppUiMetadata("presentation", "flyout");
		}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();

		ui["presentation"]!.GetValue<string>().Should().Be("flyout");
	}

	[TestMethod]
	[Description("WithMcpAppPermissions can include browser permission metadata.")]
	public async Task When_CommandHasPermissions_Then_ResourceMetaIncludesThem()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource()
				.WithMcpAppPermissions(new McpAppPermissions { ClipboardWrite = true });
		}).ConfigureAwait(false);

		var result = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		var content = result.Contents.OfType<TextResourceContents>().Single();
		var ui = content.Meta!["ui"]!.AsObject();
		var permissions = ui["permissions"]!.AsObject();

		permissions["clipboardWrite"]!.AsObject().Should().BeEmpty();
	}

	[TestMethod]
	[Description("AsMcpAppResource exposes a launcher tool that does not return raw HTML.")]
	public async Task When_McpAppResourceToolIsCalled_Then_ModelToolDoesNotReturnHtml()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.WithDescription("Open the contacts dashboard")
				.AsMcpAppResource();
		}).ConfigureAwait(false);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var launcher = tools.Single(tool =>
			string.Equals(tool.Name, "contacts_dashboard", StringComparison.Ordinal));
		var launcherUi = launcher.ProtocolTool.Meta!["ui"]!.AsObject();

		launcherUi["visibility"]!.AsArray().Select(static node => node!.GetValue<string>())
			.Should().BeEquivalentTo(["model", "app"]);

		var toolResult = await fixture.Client.CallToolAsync("contacts_dashboard").ConfigureAwait(false);
		toolResult.Content.OfType<TextContentBlock>().Single().Text
			.Should().Contain("Open the contacts dashboard")
			.And.NotContain("<html");

		var resourceResult = await fixture.Client.ReadResourceAsync("ui://contacts/dashboard").ConfigureAwait(false);
		resourceResult.Contents.OfType<TextResourceContents>().Single().Text
			.Should().Contain("Contacts");
	}

	[TestMethod]
	[Description("WithMcpAppLauncherText customizes the launcher tool fallback text.")]
	public async Task When_McpAppLauncherTextIsConfigured_Then_ToolReturnsThatText()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts dashboard", () => "<html><body>Contacts</body></html>")
				.AsMcpAppResource()
				.WithMcpAppLauncherText("Opening the dashboard.");
		}).ConfigureAwait(false);

		var toolResult = await fixture.Client.CallToolAsync("contacts_dashboard").ConfigureAwait(false);

		toolResult.Content.OfType<TextContentBlock>().Single().Text
			.Should().Be("Opening the dashboard.");
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
