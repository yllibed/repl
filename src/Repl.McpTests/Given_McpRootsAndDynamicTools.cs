using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Mcp;

// These tests exercise Roots/Sampling/Logging, deprecated by MCP spec 2026-07-28
// (SEP-2577, MCP9005) but still supported by Repl.Mcp until the SDK removes them.
// Tracked in issue #51.
#pragma warning disable MCP9005

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpRootsAndDynamicTools
{
	[TestMethod]
	[Description("Native client roots are available to MCP command handlers when the client supports roots.")]
	public async Task When_ClientSupportsRoots_Then_RootAwareToolCanReadThem()
	{
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
					Roots =
					[
						new Root
						{
							Uri = "file:///C:/workspace",
							Name = "workspace",
						},
					],
				}),
			},
		};

		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.MapModule(new RootAwareModule());
			},
			configureOptions: null,
			clientOptions: clientOptions);

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		tools.Should().ContainSingle(t => string.Equals(t.Name, "roots_info", StringComparison.Ordinal));

		var result = await fixture.Client.CallToolAsync(
			toolName: "roots_info",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var text = result.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("workspace");
		text.Should().Contain("file:///C:/workspace");
	}

	[TestMethod]
	[Description("Regression guard: a handler that reads IMcpClientRoots.Current WITHOUT calling GetAsync must still see the client's workspace on its very first invocation. A modern revision forbids discovery from depending on the connection, so discovery makes no roots round-trip at all and the resolution happens at the execution boundary instead. The sibling test above calls GetAsync explicitly, which is exactly why it cannot catch this.")]
	public async Task When_AModernHandlerReadsCurrentWithoutAsking_Then_TheFirstCallSeesTheClientRoots()
	{
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

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new CurrentOnlyRootsModule()),
			configureOptions: null,
			clientOptions: clientOptions);

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		// The FIRST call: nothing has resolved roots for this connection yet.
		var result = await fixture.Client.CallToolAsync(
			toolName: "roots_peek",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		result.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"file:///C:/workspace",
			because: "Current is documented as the client's effective roots, not as a cache the handler must prime");
	}

	[TestMethod]
	[Description("Soft roots can initialize MCP-only commands when native roots are unavailable. Pinned to an initialize-era revision: that is where a per-session tool graph is legal, and where revealing a command as a side effect of a tools/call is not forbidden.")]
	public async Task When_LegacyClientDoesNotSupportRoots_Then_SoftRootsCanInitializeWorkspace()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			configure: app =>
			{
				app.MapModule(
					new SoftRootsInitModule(),
					(IMcpClientRoots roots) => !roots.IsSupported);
				app.MapModule(
					new SoftRootsWorkspaceModule(),
					(IMcpClientRoots roots) => !roots.IsSupported && roots.HasSoftRoots);
			},
			configureOptions: null,
			clientOptions: new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions });

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		var before = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		before.Should().Contain(t => string.Equals(t.Name, "softroots_init", StringComparison.Ordinal));
		before.Should().NotContain(t => string.Equals(t.Name, "softroots_show", StringComparison.Ordinal));

		await fixture.Client.CallToolAsync(
			"softroots_init",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["path"] = "file:///C:/soft-workspace",
			}).ConfigureAwait(false);

		var after = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		after.Should().Contain(t => string.Equals(t.Name, "softroots_show", StringComparison.Ordinal));

		var show = await fixture.Client.CallToolAsync(
			toolName: "softroots_show",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var text = show.Content.OfType<TextContentBlock>().First().Text;
		text.Should().Contain("file:///C:/soft-workspace");
	}

	[TestMethod]
	[Description("Regression guard: soft roots keep working on 2026-07-28, where they no longer reveal commands. That revision forbids the advertised set from varying as a side effect of other requests on the connection — which a graph gated on HasSoftRoots would do, with a single connection being enough to observe it. Mapped unconditionally, the same commands still set the soft roots and still read them back, and the set is identical before and after.")]
	public async Task When_ModernClientSetsSoftRoots_Then_TheyResolveWithoutChangingTheAdvertisedSet()
	{
		await using var fixture = await McpTestFixture.CreateAsync(configure: app =>
		{
			app.MapModule(new SoftRootsInitModule());
			app.MapModule(new SoftRootsWorkspaceModule());
		});

		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var before = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		before.Should().Contain(t => string.Equals(t.Name, "softroots_init", StringComparison.Ordinal));
		before.Should().Contain(t => string.Equals(t.Name, "softroots_show", StringComparison.Ordinal));

		await fixture.Client.CallToolAsync(
			"softroots_init",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["path"] = "file:///C:/soft-workspace",
			}).ConfigureAwait(false);

		var after = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		after.Select(static t => t.Name).Should().BeEquivalentTo(
			before.Select(static t => t.Name),
			because: "a tools/call must not change the set advertised on the same connection");

		var show = await fixture.Client.CallToolAsync(
			toolName: "softroots_show",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		show.Content.OfType<TextContentBlock>().First().Text.Should().Contain(
			"file:///C:/soft-workspace",
			because: "execution still sees the soft roots; only the automatic revealing is gone");
	}

	[TestMethod]
	[Description("The opt-in compatibility shim exposes discover_tools and call_tool before the real tool list is refreshed.")]
	public async Task When_DynamicToolCompatibilityEnabled_Then_ClientCanDiscoverAndCallThroughShim()
	{
		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("echo {msg}", (string msg) => $"echo:{msg}");
			},
			configureOptions: options => options.DynamicToolCompatibility = DynamicToolCompatibilityMode.DiscoverAndCallShim,
			// The shim exists for clients that do not refresh a changing tool list — i.e. initialize-era
			// clients. Pinning the CLIENT (not the server) keeps the server multi-revision while making
			// this test state which revision its unsolicited-notification expectation belongs to.
			clientOptions: new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions });

		await using var registration = fixture.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				listChanged.TrySetResult();
				return ValueTask.CompletedTask;
			});

		var firstTools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		firstTools.Select(static tool => tool.Name).Should().BeEquivalentTo(
			[ "discover_tools", "call_tool" ]);

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		var discover = await fixture.Client.CallToolAsync(
			toolName: "discover_tools",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		discover.IsError.Should().NotBeTrue();
		discover.StructuredContent.Should().NotBeNull();
		var discoveredTools = JsonSerializer.Deserialize<Tool[]>(
			discover.StructuredContent!.Value.GetRawText(),
			McpJsonUtilities.DefaultOptions);
		discoveredTools.Should().NotBeNull();
		discoveredTools!.Should().Contain(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));

		var compatibilityCall = await fixture.Client.CallToolAsync(
			toolName: "call_tool",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["name"] = "echo",
				["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["msg"] = "hello",
				},
			}).ConfigureAwait(false);
		compatibilityCall.IsError.Should().NotBeTrue();
		compatibilityCall.Content.OfType<TextContentBlock>().First().Text.Should().Contain("echo:hello");

		var secondTools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		secondTools.Should().Contain(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));
		secondTools.Should().NotContain(t => string.Equals(t.Name, "discover_tools", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("The compatibility shim is re-armed after routing invalidation so dynamic clients can bootstrap again.")]
	public async Task When_RoutingChanges_AfterCompatibilityIntro_Then_ShimIsServedAgain()
	{
		var listChangedCount = 0;
		var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("echo {msg}", (string msg) => $"echo:{msg}");
			},
			configureOptions: options => options.DynamicToolCompatibility = DynamicToolCompatibilityMode.DiscoverAndCallShim,
			// The shim exists for clients that do not refresh a changing tool list — i.e. initialize-era
			// clients. Pinning the CLIENT (not the server) keeps the server multi-revision while making
			// this test state which revision its unsolicited-notification expectation belongs to.
			clientOptions: new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions });

		await using var registration = fixture.Client.RegisterNotificationHandler(
			NotificationMethods.ToolListChangedNotification,
			(_, _) =>
			{
				var count = Interlocked.Increment(ref listChangedCount);
				listChanged.TrySetResult();
				if (count >= 2)
				{
					return ValueTask.CompletedTask;
				}

				return ValueTask.CompletedTask;
			});

		var initialTools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		initialTools.Select(static tool => tool.Name).Should().BeEquivalentTo(
			["discover_tools", "call_tool"]);

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		var steadyStateTools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		steadyStateTools.Should().Contain(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));
		steadyStateTools.Should().NotContain(t => string.Equals(t.Name, "discover_tools", StringComparison.Ordinal));

		listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.App.Map("added later", () => "added");
		fixture.App.Core.InvalidateRouting();

		await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		var toolsAfterInvalidation = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		toolsAfterInvalidation.Select(static tool => tool.Name).Should().BeEquivalentTo(
			["discover_tools", "call_tool"]);
	}

	private sealed class RootAwareModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"roots info",
				async (IMcpClientRoots roots, CancellationToken cancellationToken) =>
				{
					var currentRoots = await roots.GetAsync(cancellationToken).ConfigureAwait(false);
					return string.Join(
						Environment.NewLine,
						currentRoots.Select(static root => $"{root.Name}:{root.Uri}"));
				}).ReadOnly();
		}
	}

	private sealed class CurrentOnlyRootsModule : IReplModule
	{
		public void Map(IReplMap app) =>
			app.Map(
				"roots peek",
				(IMcpClientRoots roots) =>
					string.Join(',', roots.Current.Select(static root => root.Uri.ToString())))
				.ReadOnly();
	}

	private sealed class SoftRootsInitModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"softroots init {path}",
				(IMcpClientRoots roots, string path) =>
				{
					roots.SetSoftRoots([new McpClientRoot(Uri: new Uri(path, UriKind.Absolute), Name: "soft-root")]);
					return "initialized";
				});
		}
	}

	private sealed class SoftRootsWorkspaceModule : IReplModule
	{
		public void Map(IReplMap app)
		{
			app.Map(
				"softroots show",
				(IMcpClientRoots roots) => roots.Current.Select(static root => root.Uri.ToString()).FirstOrDefault() ?? "none")
				.ReadOnly();
		}
	}
}
