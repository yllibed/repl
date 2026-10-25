using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Repl.Interaction;
using Repl.Mcp;
using Repl.Parameters;

namespace Repl.McpTests;

/// <summary>
/// End-to-end integration tests that spin up a real MCP server from a Repl app
/// and connect a real MCP client via in-process pipes.
/// </summary>
[TestClass]
public sealed class Given_McpServerEndToEnd
{
	[TestMethod]
	[Description("tools/list returns all non-hidden, non-AutomationHidden commands.")]
	public async Task When_ToolsList_Then_ReturnsExpectedTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("greet {name}", (string name) => $"Hello, {name}!")
				.WithDescription("Greet someone")
				.ReadOnly();
			app.Map("hidden-cmd", () => "secret").Hidden();
			app.Map("wizard", () => "interactive").AutomationHidden();
		});

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "greet", StringComparison.Ordinal));
		tools.Should().NotContain(t => string.Equals(t.Name, "hidden-cmd", StringComparison.Ordinal));
		tools.Should().NotContain(t => string.Equals(t.Name, "wizard", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("tools/list includes correct JSON Schema with types and format hints.")]
	public async Task When_ToolsList_Then_SchemaIsCorrect()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contact {id:guid}", (Guid id) => new { Id = id })
				.WithDescription("Get contact")
				.ReadOnly();
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "contact", StringComparison.Ordinal));
		var schema = tool.JsonSchema;

		schema.GetProperty("properties").GetProperty("id").GetProperty("type").GetString()
			.Should().Be("string");
		schema.GetProperty("properties").GetProperty("id").GetProperty("format").GetString()
			.Should().Be("uuid");
		schema.GetProperty("properties").TryGetProperty("_replCursor", out _)
			.Should().BeFalse("non-paged MCP tools should not expose Repl continuation cursors");
		schema.GetProperty("properties").TryGetProperty("_replPageSize", out _)
			.Should().BeFalse("non-paged MCP tools should not expose Repl page sizing");
		schema.GetProperty("required")[0].GetString()
			.Should().Be("id");
	}

	[TestMethod]
	[Description("tools/call dispatches through the Repl pipeline and returns output.")]
	public async Task When_ToolsCall_Then_ReturnsCommandOutput()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("greet {name}", (string name) => $"Hello, {name}!")
				.ReadOnly();
		});

		var result = await fixture.Client.CallToolAsync(
			"greet",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Alice" });

		var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
		textBlock.Should().NotBeNull("the tool call should produce text content");
		textBlock!.Text.Should().Contain("Hello, Alice!");
	}

	[TestMethod]
	[Description("tools/call returns paged results as structured content with a continuation summary.")]
	public async Task When_ToolsCallReturnsPagedResult_Then_StructuredContentContainsPageInfo()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts", (IReplPagingContext paging) =>
				paging.Page(
					new[]
					{
						new ContactDto(1, "Alice"),
					},
					nextCursor: "page-2",
					totalCount: 2))
				.ReadOnly();
		});

		var result = await fixture.Client.CallToolAsync(
			"contacts",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["_replPageSize"] = 1,
				["_replCursor"] = "start",
			});

		result.IsError.Should().NotBeTrue();
		result.StructuredContent.Should().NotBeNull();
		var root = result.StructuredContent!.Value;
		root.GetProperty("items").GetArrayLength().Should().Be(1);
		root.GetProperty("pageInfo").GetProperty("cursor").GetString().Should().Be("start");
		root.GetProperty("pageInfo").GetProperty("nextCursor").GetString().Should().Be("page-2");
		root.GetProperty("pageInfo").GetProperty("totalCount").GetInt64().Should().Be(2);
		var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
			?? throw new AssertFailedException("Expected a text content block.");
		text.Should().Contain("page-2");
		text.Should().Contain("\"items\"");
	}

	[TestMethod]
	[Description("tools/call can use summary-only paged text content for low-token clients.")]
	public async Task When_PagedResultTextModeIsSummaryOnly_Then_RawCursorStaysOutOfText()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("contacts", (IReplPagingContext paging) =>
					paging.Page(
						new[] { new ContactDto(1, "Alice") },
						nextCursor: "page-2",
						totalCount: 2))
					.ReadOnly();
			},
			options => options.PagedResultTextMode = McpPagedResultTextMode.SummaryOnly);

		var result = await fixture.Client.CallToolAsync(
			"contacts",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["_replPageSize"] = 1,
			});

		result.StructuredContent.Should().NotBeNull();
		var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
			?? throw new AssertFailedException("Expected a text content block.");
		text.Should().Contain("Returned 1 item(s).");
		text.Should().Contain("cursor available");
		text.Should().NotContain("page-2");
		text.Should().NotContain("\"items\"");
	}

	[TestMethod]
	[Description("tools/call does not treat arbitrary JSON objects with items and pageInfo properties as paged results.")]
	public async Task When_ToolsCallReturnsPageShapedObject_Then_ResultIsPlainText()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map(
					"shape",
					() => new
					{
						Items = PageShapedItems,
						PageInfo = new { NextCursor = "raw-cursor" },
					})
				.ReadOnly();
		});

		var result = await fixture.Client.CallToolAsync(
			"shape",
			new Dictionary<string, object?>(StringComparer.Ordinal));

		result.StructuredContent.Should().BeNull();
		result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("not-a-page");
	}

	[TestMethod]
	[Description("tools/call returns page-source results as structured pages and consumes MCP cursor arguments.")]
	public async Task When_ToolsCallReturnsPageSource_Then_CursorFetchesNextPage()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts", () => ReplPageSource.FromItems(
				[
					new ContactDto(1, "Alice"),
					new ContactDto(2, "Bob"),
				]))
				.ReadOnly();
		});

		var first = await fixture.Client.CallToolAsync(
			"contacts",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["_replPageSize"] = 1,
			});
		var firstRoot = first.StructuredContent!.Value;
		var nextCursor = firstRoot.GetProperty("pageInfo").GetProperty("nextCursor").GetString();

		var second = await fixture.Client.CallToolAsync(
			"contacts",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["_replPageSize"] = 1,
				["_replCursor"] = nextCursor,
			});

		second.IsError.Should().NotBeTrue();
		var secondRoot = second.StructuredContent!.Value;
		secondRoot.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Bob");
		secondRoot.GetProperty("pageInfo").GetProperty("cursor").GetString().Should().Be(nextCursor);
		secondRoot.GetProperty("pageInfo").GetProperty("hasMore").GetBoolean().Should().BeFalse();
	}

	[TestMethod]
	[Description("Context commands are flattened into underscore-separated tool names.")]
	public async Task When_ContextCommands_Then_FlattenedToolNames()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Context("contact", ctx =>
			{
				ctx.Map("add", (string name) => name).OpenWorld();
				ctx.Map("list", () => "all").ReadOnly();
			});
		});

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().Contain(t => string.Equals(t.Name, "contact_add", StringComparison.Ordinal));
		tools.Should().Contain(t => string.Equals(t.Name, "contact_list", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("tools/call on a context command dispatches correctly.")]
	public async Task When_ToolsCallContextCommand_Then_DispatchesCorrectly()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Context("math", ctx =>
			{
				ctx.Map("add", (int a, int b) => a + b).ReadOnly();
			});
		});

		var result = await fixture.Client.CallToolAsync(
			"math_add",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["a"] = 3, ["b"] = 7 });

		var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
		textBlock.Should().NotBeNull("the tool call should produce text content");
		textBlock!.Text.Should().Contain("10");
	}

	[TestMethod]
	[Description("Tool description combines Description and Details.")]
	public async Task When_ToolHasDetails_Then_DescriptionIncludesBoth()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("deploy", () => "ok")
				.WithDescription("Deploy app")
				.WithDetails("Deploys to the specified environment.");
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "deploy", StringComparison.Ordinal));

		tool.Description.Should().Contain("Deploy app");
		tool.Description.Should().Contain("Deploys to the specified environment.");
	}

	[TestMethod]
	[Description("Optional parameters are not in the required array.")]
	public async Task When_OptionalParameter_Then_NotRequired()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("search", (string query, int? limit) => $"{query} limit={limit}")
				.ReadOnly();
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "search", StringComparison.Ordinal));
		var schema = tool.JsonSchema;

		// "query" should not be required (string is reference type → optional by default).
		// "limit" should not be required (nullable).
		if (schema.TryGetProperty("required", out var required))
		{
			var requiredNames = Enumerable.Range(0, required.GetArrayLength())
				.Select(i => required[i].GetString())
				.ToList();
			requiredNames.Should().NotContain("limit");
		}
	}

	[TestMethod]
	[Description("ReadOnly commands are exposed as tools (they're regular commands with an annotation).")]
	public async Task When_ReadOnlyCommand_Then_ExposedAsTool()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("contacts", () => "Alice, Bob")
				.WithDescription("List contacts")
				.ReadOnly()
				.AsResource();
		});

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "contacts", StringComparison.Ordinal));
	}

	// ── Prompts ────────────────────────────────────────────────────────

	// ── Context parameter binding ─────────────────────────────────────

	[TestMethod]
	[Description("Context tool call with optional params present dispatches correctly.")]
	public async Task When_ContextToolCallWithOptionalParams_Then_Succeeds()
	{
		await using var fixture = await CreateContextFixtureAsync();

		var result = await fixture.Client.CallToolAsync(
			"session_screenshot",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["id"] = "s1",
				["path"] = @"C:\out\file.png",
				["zone"] = "status",
			});

		result.IsError.Should().BeFalse("call with optional param should succeed");
		var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		text.Should().NotBeNull();
		text!.Should().Contain("s1");
		text.Should().Contain("file.png");
	}

	[TestMethod]
	[Description("Context tool call without optional params dispatches correctly.")]
	public async Task When_ContextToolCallWithoutOptionalParams_Then_Succeeds()
	{
		await using var fixture = await CreateContextFixtureAsync();

		var result = await fixture.Client.CallToolAsync(
			"session_screenshot",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["id"] = "s1",
				["path"] = @"C:\out\file.png",
			});

		result.IsError.Should().BeFalse("omitting optional params should not cause a binding failure");
		var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		text.Should().NotBeNull();
		text!.Should().Contain("s1");
		text.Should().Contain("file.png");
		text.Should().Contain("null", "optional string? params should be null when omitted, not resolved from context");
	}

	[TestMethod]
	[Description("[FromServices] parameters must not appear in tool schema properties.")]
	public async Task When_HandlerHasFromServicesParams_Then_SchemaExcludesThem()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Context("session", session =>
			{
				session.Context("{id}", scoped =>
				{
					scoped.Map("screenshot", async (
						string id,
						[System.ComponentModel.Description("File path")] string path,
						[System.ComponentModel.Description("Named zone")] string? zone,
						[FromServices] MarkerService svc,
						[FromServices] AnotherService svc2) =>
					{
						await Task.CompletedTask.ConfigureAwait(false);
						return (object)new { id, path, zone };
					});
				});
			});
		}, configureServices: services =>
		{
			services.AddSingleton<MarkerService>();
			services.AddSingleton<AnotherService>();
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "session_screenshot", StringComparison.Ordinal));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("id", out _).Should().BeTrue("id is a route argument");
		properties.TryGetProperty("path", out _).Should().BeTrue("path is a command option");
		properties.TryGetProperty("zone", out _).Should().BeTrue("zone is a command option");
		properties.TryGetProperty("svc", out _).Should().BeFalse("[FromServices] params must not leak into schema");
		properties.TryGetProperty("svc2", out _).Should().BeFalse("[FromServices] params must not leak into schema");
	}

	private static Task<McpTestFixture> CreateContextFixtureAsync() =>
		McpTestFixture.CreateAsync(app =>
		{
			app.Context("session", session =>
			{
				session.Map("list", ([FromServices] MarkerService svc) => "all")
					.WithDescription("List sessions").ReadOnly();

				session.Context("{id}", scoped =>
				{
					scoped.Map("info", (
						string id,
						[System.ComponentModel.Description("Process id")] int? pid,
						[System.ComponentModel.Description("Show details")] bool? detailed,
						[FromServices] MarkerService svc) =>
						(object)new { id, pid, detailed }).ReadOnly();

					scoped.Map("screenshot", async (
						string id,
						[System.ComponentModel.Description("File path")] string path,
						[System.ComponentModel.Description("Crop region")] string? region,
						[System.ComponentModel.Description("Named zone")] string? zone,
						[FromServices] MarkerService svc,
						[FromServices] AnotherService svc2) =>
					{
						await Task.CompletedTask.ConfigureAwait(false);
						return (object)new { id, path, zone, region };
					});
				});
			});
		}, configureServices: services =>
		{
			services.AddSingleton<MarkerService>();
			services.AddSingleton<AnotherService>();
		});

	private sealed class MarkerService
	{
		public static string Marker => "ok";
	}

	private sealed class AnotherService;

	private sealed record ContactDto(int Id, string Name);

	private static readonly string[] PageShapedItems = ["not-a-page"];

	// ── Prompts ────────────────────────────────────────────────────────

	[TestMethod]
	[Description("prompts/list returns prompt with correct arguments from the command's parameters.")]
	public async Task When_PromptsList_Then_ReturnsPromptWithArguments()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("troubleshoot {symptom}", (string symptom) => $"Diagnose: {symptom}")
				.WithDescription("Diagnose an issue")
				.AsPrompt();
		});

		var prompts = await fixture.Client.ListPromptsAsync();

		var prompt = prompts.Should().ContainSingle(p =>
			string.Equals(p.Name, "troubleshoot", StringComparison.Ordinal)).Which;
		prompt.ProtocolPrompt.Description.Should().Be("Diagnose an issue");
		var argument = prompt.ProtocolPrompt.Arguments.Should().ContainSingle(a =>
			string.Equals(a.Name, "symptom", StringComparison.Ordinal)).Which;
		argument.Required.Should().BeTrue("non-optional Repl route arguments are required when invoking the prompt");
	}

	[TestMethod]
	[Description("prompts/get dispatches through the pipeline and returns the handler output.")]
	public async Task When_PromptsGet_Then_ReturnsHandlerOutput()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("troubleshoot {symptom}", (string symptom) => $"Diagnose: {symptom}")
				.AsPrompt();
		});

		var result = await fixture.Client.GetPromptAsync(
			"troubleshoot",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["symptom"] = "missing data" });

		result.Messages.Should().ContainSingle();
		var text = (result.Messages[0].Content as TextContentBlock)?.Text;
		text.Should().NotBeNull();
		text.Should().Contain("Diagnose: missing data");
	}

	[TestMethod]
	[Description("prompts/get unwraps JSON string literals so prompt text is plain text.")]
	public async Task When_PromptReturnsString_Then_TextIsPlainString()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("ops troubleshoot {symptom}", static (string symptom) =>
					$"Investigate the checkout service for this symptom: '{symptom}'. Start with ops_status, inspect failed checks, then propose the smallest safe next step.")
				.AsPrompt();
		});

		var result = await fixture.Client.GetPromptAsync(
			"ops_troubleshoot",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["symptom"] = "queue depth rising" });

		result.Messages.Should().ContainSingle();
		var text = (result.Messages[0].Content as TextContentBlock)?.Text;
		text.Should().Be("Investigate the checkout service for this symptom: 'queue depth rising'. Start with ops_status, inspect failed checks, then propose the smallest safe next step.");
	}

	[TestMethod]
	[Description("Hidden command options are omitted from MCP tool schemas while visible sibling options remain discoverable.")]
	public async Task When_CommandOptionIsHidden_Then_McpToolSchemaOmitsIt()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map(
					"deploy",
					([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internalMode")] bool internalMode = false) =>
						$"{environment}:{internalMode}")
				.WithOption("internalMode", static option => option.Hidden());
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("environment", out _).Should().BeTrue();
		properties.TryGetProperty("internalMode", out _).Should().BeFalse();
	}

	[TestMethod]
	[Description("A hidden option is absent from the advertised schema, so supplying it to tools/call is rejected — the same hard block a hidden command gets. This pins the guarantee that the advertised schema and the accepted argument list can never diverge, because the generated schema omits additionalProperties:false and the allow-list is the only thing enforcing it.")]
	public async Task When_HiddenCommandOptionIsSuppliedToToolCall_Then_CallIsRejected()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map(
					"deploy",
					([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internalMode")] bool internalMode = false) =>
						$"{environment}:{internalMode}")
				.WithOption("internalMode", static option => option.Hidden());
		});

		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["environment"] = "denim",
				["internalMode"] = true,
			}).ConfigureAwait(false);

		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		result.IsError.Should().BeTrue();
		text.Should().NotContain("denim", "the handler must not run when an undeclared argument is supplied");

		// The adapter's own diagnostic ("The MCP argument 'internalMode' is not defined by the
		// tool schema.") is deliberately not asserted: the SDK replaces it with a generic
		// "An error occurred invoking '<tool>'." before it reaches the client. That is the right
		// outcome for a hidden option — a caller probing for one learns nothing from the failure.
		text.Should().Contain("error occurred invoking");
	}

	[TestMethod]
	[Description("Human documentation may retain reverse/value-only reachability, but MCP cannot reconstruct an arbitrary semantic value without an ordinary named token; the optional field is therefore omitted from both schema and allow-list while the tool remains callable.")]
	public async Task When_OnlyReverseAliasRemainsReachable_Then_McpOmitsTheSemanticOption()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options =>
			{
				options.Parsing.AddGlobalOption<bool>("force");
				options.Parsing.GlobalOption("force").Hidden();
			});
			app.Map(
				"deploy",
				static string ([ReplOption(ReverseAliases = ["--no-force"])] bool force = true) => force.ToString());
		});

		var advertised = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		advertised.Should().NotContain("force");
		result.IsError.Should().NotBeTrue();
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text))
			.Should().Contain("True");
	}

	[TestMethod]
	[Description("Changing inherited option casing after an MCP snapshot retracts aliases that become equivalent to a hidden alias; the next list and call must use the rebuilt schema rather than the stale adapter.")]
	public async Task When_OptionCaseModeChangesAfterInitialList_Then_McpRetractsNewlyHiddenField()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options =>
			{
				options.Parsing.AddGlobalOption<string>("tenant");
				options.Parsing.GlobalOption("tenant").Hidden();
			});
			app.Map(
				"deploy",
				static string ([ReplOption(Name = "tenant", Aliases = ["--ACCOUNT"], HiddenAliases = ["--account"])] string? tenant = null) => tenant ?? "none");
		});

		var before = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		before.Should().Contain("tenant");

		fixture.App.Options(options =>
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);

		var after = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		var call = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["tenant"] = "north",
			}).ConfigureAwait(false);

		after.Should().NotContain("tenant");
		call.IsError.Should().BeTrue();
	}

	[TestMethod]
	[Description("The MCP half of the AutomationHidden pair: the advertised tool schema omits the option, and because the schema and the accepted argument list are built from the same option list, tools/call rejects it too. The help half is asserted separately, where the same option stays listed and binds.")]
	public async Task When_CommandOptionIsAutomationHidden_Then_McpOmitsItAndRejectsIt()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map(
					"deploy",
					([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internalMode")] bool internalMode = false) =>
						$"{environment}:{internalMode}")
				.WithOption("internalMode", static option => option.AutomationHidden());
		});

		var advertised = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["environment"] = "denim",
				["internalMode"] = true,
			}).ConfigureAwait(false);

		advertised.Should().Contain("environment");
		advertised.Should().NotContain("internalMode");
		result.IsError.Should().BeTrue();
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text))
			.Should().NotContain("denim", "the handler must not run for an argument the schema never advertised");
	}

	[TestMethod]
	[Description("An automation-hidden option that cannot be omitted leaves no valid MCP invocation: the client cannot supply it, and omitting it fails to bind. Advertising such a tool guarantees every call fails, so the command is withdrawn from MCP instead — the human command line keeps working, which is why this is not rejected at configuration time the way an all-surfaces hidden required option is.")]
	public async Task When_AutomationHiddenOptionCannotBeOmitted_Then_TheToolIsWithdrawn()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("ping", () => "pong");
			app.Map(
					"deploy",
					([ReplOption(Name = "internal-token", Arity = ReplArity.ExactlyOne)] string internalToken) => internalToken)
				.WithOption("internalToken", static option => option.AutomationHidden());
		});

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		tools.Should().Contain(tool => string.Equals(tool.Name, "ping", StringComparison.Ordinal));
		tools.Should().NotContain(tool => string.Equals(tool.Name, "deploy", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("An MCP CommandFilter defines the discovery boundary before hidden-required validation: an excluded CLI-only command cannot prevent server startup, while unrelated tools remain available.")]
	public async Task When_CommandFilterExcludesCliOnlyRequiredHiddenOption_Then_ServerStartsWithRemainingTools()
	{
		var filterCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("ping", static () => "pong");
				app.Map(
					"local",
					static string ([ReplOption(Name = "token", Hidden = true, Arity = ReplArity.ExactlyOne)] string token) => token);
			},
			options => options.CommandFilter = command =>
			{
				filterCalls[command.Path] = filterCalls.GetValueOrDefault(command.Path) + 1;
				return !string.Equals(command.Path, "local", StringComparison.OrdinalIgnoreCase);
			});

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		tools.Should().ContainSingle(tool => string.Equals(tool.Name, "ping", StringComparison.Ordinal));
		tools.Should().NotContain(tool => string.Equals(tool.Name, "local", StringComparison.Ordinal));
		filterCalls["ping"].Should().Be(1);
		filterCalls["local"].Should().Be(1);
	}

	[TestMethod]
	[Description("When CommandFilter retains a command with an impossible hidden-required contract, root-aware discovery still rejects it and evaluates the predicate only once.")]
	public async Task When_CommandFilterIncludesCliOnlyRequiredHiddenOption_Then_DiscoveryRejectsIt()
	{
		var filterCalls = 0;
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map(
				"deploy",
				static string ([ReplOption(Name = "token", Hidden = true, Arity = ReplArity.ExactlyOne)] string token) => token),
			options => options.CommandFilter = _ =>
			{
				filterCalls++;
				return true;
			});
		var discover = async () => await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		await discover.Should().ThrowAsync<McpException>().ConfigureAwait(false);
		filterCalls.Should().Be(1);
	}

	[TestMethod]
	[Description("A DI service fallback makes an explicitly required option omittable at the same precedence point used by HandlerArgumentBinder. AutomationHidden must omit that option without withdrawing the tool, and tools/call without the argument must receive the service value.")]
	public async Task When_AutomationHiddenRequiredOptionHasAServiceFallback_Then_TheToolRemainsInvocable()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map(
					"deploy",
					([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne)] string token) => token)
				.WithOption("token", static option => option.AutomationHidden()),
			configureServices: static services => services.AddSingleton<string>("service-token"));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		tool.JsonSchema.GetProperty("properties").TryGetProperty("token", out _).Should().BeFalse();
		result.IsError.Should().BeFalse();
		text.Should().Contain("service-token");
	}

	[TestMethod]
	[Description("MCP metadata and its call allow-list must omit a route option whose canonical token belongs to a higher-precedence hidden global. Otherwise the adapter accepts a field that GlobalOptionParser consumes before route binding.")]
	public async Task When_HiddenGlobalOwnsRouteOptionToken_Then_McpOmitsAndRejectsThatArgument()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options =>
			{
				options.Parsing.AddGlobalOption<string>("tenant");
				options.Parsing.GlobalOption("tenant").Hidden();
			});
			app.Map(
				"deploy",
				static string (
					[ReplOption] string? tenant = null,
					[ReplOption] string? region = null) => $"{tenant}:{region}");
		});

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["tenant"] = "acme",
			}).ConfigureAwait(false);
		tool.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeFalse();
		tool.JsonSchema.GetProperty("properties").TryGetProperty("region", out _).Should().BeTrue();
		result.IsError.Should().BeTrue();
	}

	[TestMethod]
	[Description("A hidden legacy alias remains a CLI fallback but is omitted from the MCP schema and allow-list; the visible canonical argument remains callable.")]
	public async Task When_RouteAliasIsHidden_Then_McpExposesOnlyTheCanonicalArgument()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map(
			"deploy",
			static string ([ReplOption(Name = "tenant", HiddenAliases = ["--account"])] string? tenant = null) => tenant ?? "none"));

		var tool = (await fixture.Client.ListToolsAsync().ConfigureAwait(false)).Single();
		var canonical = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "acme" })
			.ConfigureAwait(false);
		var legacy = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["account"] = "acme" })
			.ConfigureAwait(false);

		tool.JsonSchema.GetProperty("properties").TryGetProperty("tenant", out _).Should().BeTrue();
		tool.JsonSchema.GetProperty("properties").TryGetProperty("account", out _).Should().BeFalse();
		canonical.IsError.Should().NotBeTrue();
		legacy.IsError.Should().BeTrue();
	}

	[TestMethod]
	[Description("When a global shadows only the canonical route token, MCP keeps the stable semantic argument name, maps it to the exact surviving short alias, and does not collide with another option whose canonical name matches that alias.")]
	public async Task When_HiddenGlobalOwnsRouteCanonicalToken_Then_McpUsesTheRouteAliasWithoutRenamingTheArgument()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options =>
			{
				options.Parsing.AddGlobalOption<string>("tenant");
				options.Parsing.GlobalOption("tenant").Hidden();
			});
			app.Map(
				"deploy",
				static string (
					[ReplOption(Aliases = ["-t"])] string? tenant = null,
					[ReplOption(Name = "t")] string? shortName = null) => $"{tenant ?? "none"}:{shortName ?? "none"}");
		});

		var tool = (await fixture.Client.ListToolsAsync().ConfigureAwait(false))
			.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["tenant"] = "acme",
				["t"] = "north",
			}).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("tenant", out _).Should().BeTrue();
		properties.TryGetProperty("t", out _).Should().BeTrue();
		result.IsError.Should().NotBeTrue();
		text.Should().Contain("acme:north");
	}

	[TestMethod]
	[Description("A route argument and an option whose stable MCP name becomes identical after global canonical-token shadowing must fail closed instead of overwriting the JSON schema property and binding the submitted value positionally.")]
	public async Task When_ShadowedOptionStableNameCollidesWithRouteArgument_Then_McpStartupFailsClosed()
	{
		var start = async () =>
		{
			var fixture = await McpTestFixture.CreateAsync(app =>
			{
				app.Options(options =>
				{
					options.Parsing.AddGlobalOption<string>("scope");
					options.Parsing.GlobalOption("scope").Hidden();
				});
				app.Map(
					"deploy {scope}",
					static string (string scope, [ReplOption(Name = "scope", Aliases = ["-s"])] string? selected = null) =>
						$"{scope}:{selected ?? "none"}");
			}).ConfigureAwait(false);
			try
			{
				_ = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
			}
			finally
			{
				await fixture.DisposeAsync().ConfigureAwait(false);
			}
		};

		var faulted = await start.Should()
			.ThrowAsync<McpProtocolException>()
			.WaitAsync(TimeSpan.FromSeconds(15))
			.ConfigureAwait(false);

		faulted.WithMessage("*error occurred*");
	}

	[TestMethod]
	[Description("A case-distinct ordinary option and the synthetic MCP cursor remain separate schema fields and bind to the option and paging context respectively in one real tool call.")]
	public async Task When_OptionDiffersFromSyntheticCursorOnlyByCase_Then_EndToEndBindingKeepsBothDestinations()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map(
			"inspect",
			static string (
				[ReplOption(Name = "_replcursor")] string? ordinary,
				IReplPagingContext paging) => $"{ordinary ?? "none"}:{paging.Cursor ?? "none"}"));

		var tool = (await fixture.Client.ListToolsAsync().ConfigureAwait(false))
			.Single(candidate => string.Equals(candidate.Name, "inspect", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "inspect",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["_replcursor"] = "ordinary",
				[McpResultFlowArgumentNames.Cursor] = "opaque",
			}).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("_replcursor", out _).Should().BeTrue();
		properties.TryGetProperty(McpResultFlowArgumentNames.Cursor, out _).Should().BeTrue();
		result.IsError.Should().NotBeTrue();
		text.Should().Contain("ordinary:opaque");
	}

	[TestMethod]
	[Description("A declared answer and a case-distinct ordinary option under the answer prefix remain separate destinations through schema generation, tool adaptation, and runtime interaction lookup.")]
	public async Task When_OptionDiffersFromDeclaredAnswerOnlyByCase_Then_EndToEndBindingKeepsBothDestinations()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map(
				"wizard",
				static async Task<string> (
					[ReplOption(Name = "answer.CONFIRM")] string? ordinary,
					IReplInteractionChannel interaction) =>
				{
					var answer = await interaction.AskConfirmationAsync("confirm", "Proceed?").ConfigureAwait(false);
					return $"{ordinary ?? "none"}:{answer}";
				})
			.WithAnswer("confirm", "bool"));

		var tool = (await fixture.Client.ListToolsAsync().ConfigureAwait(false))
			.Single(candidate => string.Equals(candidate.Name, "wizard", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "wizard",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["answer.CONFIRM"] = "ordinary",
				["answer.confirm"] = false,
			}).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("answer.CONFIRM", out _).Should().BeTrue();
		properties.TryGetProperty("answer.confirm", out _).Should().BeTrue();
		result.IsError.Should().NotBeTrue();
		text.Should().Contain("ordinary:False");
	}

	[TestMethod]
	[Description("MCP supplies IReplInteractionChannel, so the binder synthesizes structured progress: the parameter is never a tool argument, and an argument-free call still receives it.")]
	public async Task When_AToolReceivesProgress_Then_ItIsNeverAToolArgument()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map(
			"sync",
			(IProgress<ReplProgressEvent> progress) =>
				progress is not null ? "progress-ready" : "missing"));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "sync", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "sync",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		tool.JsonSchema.GetProperty("properties").TryGetProperty("progress", out _).Should().BeFalse();
		result.IsError.Should().BeFalse();
		text.Should().Contain("progress-ready");
	}

	[TestMethod]
	[Description("A progress parameter is not a tool argument, so a client supplying one is refused as for any argument the schema does not define, and the handler does not run.")]
	public async Task When_AClientSuppliesAProgressArgument_Then_TheCallIsRefused()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map(
			"sync",
			(IProgress<ReplProgressEvent> progress) => "progress-ready"));

		var result = await fixture.Client.CallToolAsync(
			toolName: "sync",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["progress"] = true }).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		result.IsError.Should().BeTrue();
		text.Should().NotContain("progress-ready");
	}

	[TestMethod]
	[Description("An all-surfaces hidden required option with no service fallback would disappear from both schema and call allow-list while remaining mandatory. MCP startup must reject that provider-specific impossible contract promptly rather than advertise a dead tool.")]
	public async Task When_HiddenRequiredOptionHasNoServiceFallback_Then_McpStartupFailsFast()
	{
		var start = async () => await McpTestFixture.CreateAsync(app =>
			app.Map(
				"deploy",
				([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true)] string token) => token)).ConfigureAwait(false);

		var faulted = await start.Should()
			.ThrowAsync<InvalidOperationException>()
			.WaitAsync(TimeSpan.FromSeconds(15))
			.ConfigureAwait(false);

		faulted.WithMessage("Option target 'token' (rendered as '--token') for command 'deploy' cannot be hidden because it is required.*");
	}

	[TestMethod]
	[Description("Provider-aware requiredness also applies to visible options: if omission reaches a registered service before explicit lower-bound enforcement, MCP must not mark the field required and an argument-free call must receive that service value.")]
	public async Task When_VisibleRequiredOptionHasAServiceFallback_Then_McpSchemaMakesItOptional()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map(
				"deploy",
				([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne)] string token) => token),
			configureServices: static services => services.AddSingleton<string>("service-token"));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));
		var result = await fixture.Client.CallToolAsync(
			toolName: "deploy",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

		tool.JsonSchema.GetProperty("properties").TryGetProperty("token", out _).Should().BeTrue();
		if (tool.JsonSchema.TryGetProperty("required", out var required))
		{
			required.EnumerateArray().Select(static item => item.GetString()).Should().NotContain("token");
		}
		result.IsError.Should().BeFalse();
		text.Should().Contain("service-token");
	}

	[TestMethod]
	[Description("Hiding an option after the first tools/list must withdraw it from the next one. The MCP snapshot is rebuilt only when routing is invalidated, so a visibility change that forgets to invalidate leaves an agent seeing an option the app has retracted.")]
	public async Task When_OptionIsHiddenAfterFirstToolsList_Then_SecondToolsListOmitsIt()
	{
		CommandBuilder? deploy = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			deploy = app.Map(
				"deploy",
				([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internalMode")] bool internalMode = false) =>
					$"{environment}:{internalMode}"));

		var advertisedBefore = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		deploy!.WithOption("internalMode", option => option.Hidden());
		var advertisedAfter = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);

		advertisedBefore.Should().Contain("internalMode");
		advertisedAfter.Should().NotContain("internalMode");
		advertisedAfter.Should().Contain("environment", "hiding one option must not withdraw its siblings");
	}

	[TestMethod]
	[Description("If a post-start visibility change makes the new MCP contract impossible, refresh must fail closed rather than restore and permanently cache the old snapshot that still advertises the retracted option. Re-exposing it must then recover normally. Because a client has already received a working schema by this point, the error must not name the option, its rendered token, or the route — that identity is exactly what Hidden() was meant to withhold.")]
	public async Task When_RequiredOptionIsHiddenAfterFirstToolsList_Then_RefreshFailsClosedUntilConfigurationRecovers()
	{
		CommandBuilder? deploy = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			deploy = app.Map(
				"deploy",
				([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne)] string token) => token));
		var advertisedBefore = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);

		deploy!.WithOption("token", static option => option.Hidden());
		var firstRefresh = async () => await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var secondRefresh = async () => await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		advertisedBefore.Should().Contain("token");
		var firstFault = await firstRefresh.Should().ThrowAsync<McpException>().ConfigureAwait(false);
		var secondFault = await secondRefresh.Should().ThrowAsync<McpException>().ConfigureAwait(false);

		firstFault.Which.Message.Should().NotContainAny("token", "--token", "deploy");
		secondFault.Which.Message.Should().NotContainAny("token", "--token", "deploy");

		deploy.WithOption("token", static option => option.Hidden(isHidden: false));
		var advertisedAfterRecovery = await ReadDeployPropertiesAsync(fixture).ConfigureAwait(false);
		advertisedAfterRecovery.Should().Contain("token");
	}

	[TestMethod]
	[Description("Same contract one level up: hiding a whole command after the first tools/list withdraws its tool. This axis predates option-level visibility and shares the missing-invalidation cause, so it is pinned alongside it.")]
	public async Task When_CommandIsHiddenAfterFirstToolsList_Then_SecondToolsListOmitsIt()
	{
		CommandBuilder? wizard = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("ping", () => "pong");
			wizard = app.Map("wizard", () => "interactive");
		});

		var before = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		wizard!.Hidden();
		var after = await fixture.Client.ListToolsAsync().ConfigureAwait(false);

		before.Should().Contain(tool => string.Equals(tool.Name, "wizard", StringComparison.Ordinal));
		after.Should().NotContain(tool => string.Equals(tool.Name, "wizard", StringComparison.Ordinal));
		after.Should().Contain(tool => string.Equals(tool.Name, "ping", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("A server that fails while starting must surface its own exception from CreateAsync. The fixture used to launch RunAsync fire-and-forget and then await the client handshake, so a start failure was observable only as an initialize timeout carrying the wrong exception — which is what pushed an earlier iteration to make production code throw synchronously just to be testable.")]
	public async Task When_ServerStartFails_Then_FixtureSurfacesTheServerFault()
	{
		var start = async () => await McpTestFixture.CreateAsync(
			app => app.Map("ping", () => "pong"),
			configureOptions: static options => options.TransportFactory =
				static (_, _) => throw new InvalidOperationException("ga-bu-zo-meu: transport refused to start")).ConfigureAwait(false);

		var faulted = await start.Should()
			.ThrowAsync<InvalidOperationException>()
			.WaitAsync(TimeSpan.FromSeconds(15))
			.ConfigureAwait(false);

		faulted.WithMessage("*ga-bu-zo-meu*");
	}

	private static async Task<List<string>> ReadDeployPropertiesAsync(McpTestFixture fixture)
	{
		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var tool = tools.Single(candidate => string.Equals(candidate.Name, "deploy", StringComparison.Ordinal));

		return [.. tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(static property => property.Name)];
	}

	// ── Options group camelCase naming ─────────────────────────────────

	[TestMethod]
	[Description("Options group PascalCase properties are exposed as camelCase in MCP tool schema.")]
	public async Task When_OptionsGroupWithPascalCaseProperties_Then_SchemaUsesCamelCase()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("report", (ReportOptions opts) => $"{opts.IncludeSegments}:{opts.MaxResults}")
				.WithDescription("Generate report")
				.ReadOnly();
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "report", StringComparison.Ordinal));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("includeSegments", out _).Should().BeTrue(
			"PascalCase property 'IncludeSegments' must be exposed as camelCase 'includeSegments'");
		properties.TryGetProperty("maxResults", out _).Should().BeTrue(
			"PascalCase property 'MaxResults' must be exposed as camelCase 'maxResults'");

		properties.TryGetProperty("IncludeSegments", out _).Should().BeFalse(
			"PascalCase 'IncludeSegments' must not leak into the schema");
		properties.TryGetProperty("MaxResults", out _).Should().BeFalse(
			"PascalCase 'MaxResults' must not leak into the schema");
	}

	[TestMethod]
	[Description("Options group tool call with camelCase keys dispatches correctly.")]
	public async Task When_OptionsGroupToolCallWithCamelCaseKeys_Then_Succeeds()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("report", (ReportOptions opts) => $"{opts.IncludeSegments}|{opts.MaxResults}")
				.ReadOnly();
		});

		var result = await fixture.Client.CallToolAsync(
			"report",
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["includeSegments"] = true,
				["maxResults"] = 50,
			});

		result.IsError.Should().BeFalse("camelCase keys should bind correctly to the options group");
		var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		text.Should().NotBeNull();
		text!.Should().Contain("True");
		text.Should().Contain("50");
	}

	[TestMethod]
	[Description("Options group with explicit ReplOption.Name override uses the override in MCP schema.")]
	public async Task When_OptionsGroupWithExplicitOptionName_Then_SchemaUsesOverride()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("export", (ExportOptions opts) => opts.OutputPath ?? "default")
				.ReadOnly();
		});

		var tools = await fixture.Client.ListToolsAsync();
		var tool = tools.Single(t => string.Equals(t.Name, "export", StringComparison.Ordinal));
		var properties = tool.JsonSchema.GetProperty("properties");

		properties.TryGetProperty("out", out _).Should().BeTrue(
			"Explicit Name='out' override should be used");
		properties.TryGetProperty("OutputPath", out _).Should().BeFalse();
		properties.TryGetProperty("outputPath", out _).Should().BeFalse();
	}

	[TestMethod]
	[Description("Prompt with options group exposes camelCase argument names.")]
	public async Task When_PromptWithOptionsGroup_Then_ArgumentNamesAreCamelCase()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("analyze", (ReportOptions opts) => $"Analyze: {opts.IncludeSegments}")
				.WithDescription("Analyze data")
				.AsPrompt();
		});

		var prompts = await fixture.Client.ListPromptsAsync();
		var prompt = prompts.Single(p =>
			string.Equals(p.Name, "analyze", StringComparison.Ordinal));

		prompt.ProtocolPrompt.Arguments.Should().Contain(a =>
			string.Equals(a.Name, "includeSegments", StringComparison.Ordinal),
			"PascalCase property should be exposed as camelCase prompt argument");
		prompt.ProtocolPrompt.Arguments.Should().NotContain(a =>
			string.Equals(a.Name, "IncludeSegments", StringComparison.Ordinal),
			"PascalCase name should not leak into prompt arguments");
	}

	// ── Options group helper classes ───────────────────────────────────

	[ReplOptionsGroup]
	private sealed class ReportOptions
	{
		[System.ComponentModel.Description("Include segment details")]
		public bool IncludeSegments { get; set; }

		[System.ComponentModel.Description("Maximum results to return")]
		public int MaxResults { get; set; } = 25;
	}

	[ReplOptionsGroup]
	private sealed class ExportOptions
	{
		[ReplOption(Name = "out")]
		[System.ComponentModel.Description("Output file path")]
		public string? OutputPath { get; set; }
	}
}
