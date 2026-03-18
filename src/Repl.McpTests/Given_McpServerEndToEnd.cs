using System.Text.Json;
using ModelContextProtocol.Protocol;

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
	[Ignore("Prompt pipeline dispatch requires full CoreReplApp execution context — deferred to hosted integration tests.")]
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
		text.Should().Contain("Diagnose: missing data");
	}
}
