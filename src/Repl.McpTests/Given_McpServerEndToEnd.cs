using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
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
		prompt.ProtocolPrompt.Arguments.Should().ContainSingle(a =>
			string.Equals(a.Name, "symptom", StringComparison.Ordinal));
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
