using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

/// <summary>
/// End-to-end tests for resource→tool and prompt→tool fallback behavior.
/// Uses the real McpServerHandler pipeline via McpTestFixture.
/// </summary>
[TestClass]
public sealed class Given_McpFallbackEndToEnd
{
	// ── Resource fallback ──────────────────────────────────────────────

	[TestMethod]
	[Description("Resource-only command is NOT a tool when ResourceFallbackToTools is disabled.")]
	public async Task When_ResourceFallbackDisabled_Then_ResourceNotInTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("config", () => "data").AsResource(),
			configureOptions: null);

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().NotContain(t => string.Equals(t.Name, "config", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Resource-only command IS a tool when ResourceFallbackToTools is enabled.")]
	public async Task When_ResourceFallbackEnabled_Then_ResourceAlsoInTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("config", () => "data").AsResource(),
			configureOptions: o => o.ResourceFallbackToTools = true);

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "config", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("ReadOnly+AsResource command is always a tool regardless of fallback setting.")]
	public async Task When_ReadOnlyAsResource_Then_AlwaysATool()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("status", () => "ok").ReadOnly().AsResource(),
			configureOptions: null);

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "status", StringComparison.Ordinal));
	}

	// ── Prompt fallback ────────────────────────────────────────────────

	[TestMethod]
	[Description("Prompt-only command is NOT a tool when PromptFallbackToTools is disabled.")]
	public async Task When_PromptFallbackDisabled_Then_PromptNotInTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("explain {topic}", (string topic) => $"Explain {topic}").AsPrompt(),
			configureOptions: null);

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().NotContain(t => string.Equals(t.Name, "explain", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Prompt-only command IS a tool when PromptFallbackToTools is enabled.")]
	public async Task When_PromptFallbackEnabled_Then_PromptAlsoInTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("explain {topic}", (string topic) => $"Explain {topic}").AsPrompt(),
			configureOptions: o => o.PromptFallbackToTools = true);

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "explain", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Prompt is in prompts/list regardless of PromptFallbackToTools.")]
	public async Task When_PromptFallbackEnabled_Then_StillInPromptsList()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("explain {topic}", (string topic) => $"Explain {topic}").AsPrompt(),
			configureOptions: o => o.PromptFallbackToTools = true);

		var prompts = await fixture.Client.ListPromptsAsync();

		prompts.Should().ContainSingle(p => string.Equals(p.Name, "explain", StringComparison.Ordinal));
	}

	// ── Mixed scenarios ────────────────────────────────────────────────

	[TestMethod]
	[Description("With both fallbacks enabled, all commands are visible as tools.")]
	public async Task When_BothFallbacksEnabled_Then_AllCommandsAreTools()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("list", () => "items").ReadOnly();
				app.Map("config", () => "data").AsResource();
				app.Map("explain {topic}", (string topic) => $"Explain {topic}").AsPrompt();
			},
			configureOptions: o =>
			{
				o.ResourceFallbackToTools = true;
				o.PromptFallbackToTools = true;
			});

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().Contain(t => string.Equals(t.Name, "list", StringComparison.Ordinal));
		tools.Should().Contain(t => string.Equals(t.Name, "config", StringComparison.Ordinal));
		tools.Should().Contain(t => string.Equals(t.Name, "explain", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("AutomationHidden commands are excluded even with fallbacks enabled.")]
	public async Task When_AutomationHiddenWithFallbacks_Then_StillExcluded()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app =>
			{
				app.Map("visible", () => "ok").ReadOnly();
				app.Map("hidden-resource", () => "secret").AsResource().AutomationHidden();
				app.Map("hidden-prompt {x}", (string x) => x).AsPrompt().AutomationHidden();
			},
			configureOptions: o =>
			{
				o.ResourceFallbackToTools = true;
				o.PromptFallbackToTools = true;
			});

		var tools = await fixture.Client.ListToolsAsync();

		tools.Should().ContainSingle(t => string.Equals(t.Name, "visible", StringComparison.Ordinal));
		tools.Should().NotContain(t => string.Equals(t.Name, "hidden-resource", StringComparison.Ordinal));
		tools.Should().NotContain(t => string.Equals(t.Name, "hidden-prompt", StringComparison.Ordinal));
	}
}
