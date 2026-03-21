using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Tests the resource/prompt fallback-to-tools behavior and the
/// AutoPromoteReadOnlyToResources option.
/// Uses McpServerHandler internals to verify tool/resource/prompt generation.
/// </summary>
[TestClass]
public sealed class Given_McpFallbackOptions
{
	// ── AutoPromoteReadOnlyToResources ──────────────────────────────────

	[TestMethod]
	[Description("ReadOnly commands are auto-promoted to resources by default.")]
	public void When_ReadOnlyDefault_Then_AppearsInResources()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("status", () => "ok").ReadOnly(),
			new ReplMcpServerOptions());

		tools.Should().ContainSingle(t => string.Equals(t.ProtocolTool.Name, "status", StringComparison.Ordinal));
		resources.Should().ContainSingle();
	}

	[TestMethod]
	[Description("ReadOnly auto-promotion can be disabled.")]
	public void When_AutoPromoteDisabled_Then_ReadOnlyNotInResources()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("status", () => "ok").ReadOnly(),
			new ReplMcpServerOptions { AutoPromoteReadOnlyToResources = false });

		tools.Should().ContainSingle(t => string.Equals(t.ProtocolTool.Name, "status", StringComparison.Ordinal));
		resources.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Explicit AsResource is not affected by AutoPromoteReadOnlyToResources.")]
	public void When_ExplicitAsResource_Then_AlwaysInResources()
	{
		var (_, resources, _) = Generate(
			app => app.Map("contacts", () => "ok").AsResource(),
			new ReplMcpServerOptions { AutoPromoteReadOnlyToResources = false });

		resources.Should().ContainSingle();
	}

	// ── ResourceFallbackToTools ────────────────────────────────────────

	[TestMethod]
	[Description("Resource-only commands are NOT tools by default.")]
	public void When_ResourceOnly_Then_NotATool()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("config", () => "ok").AsResource(),
			new ReplMcpServerOptions());

		tools.Should().NotContain(t => string.Equals(t.ProtocolTool.Name, "config", StringComparison.Ordinal));
		resources.Should().ContainSingle();
	}

	[TestMethod]
	[Description("Resource-only commands become tools when ResourceFallbackToTools is enabled.")]
	public void When_ResourceFallbackEnabled_Then_ResourceAlsoATool()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("config", () => "ok").AsResource(),
			new ReplMcpServerOptions { ResourceFallbackToTools = true });

		tools.Should().ContainSingle(t => string.Equals(t.ProtocolTool.Name, "config", StringComparison.Ordinal));
		resources.Should().ContainSingle();
	}

	[TestMethod]
	[Description("ReadOnly+AsResource: tool (always) + resource (always), no duplicate tool with fallback.")]
	public void When_ReadOnlyAsResource_Then_OneToolOneResource()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("contacts", () => "ok").ReadOnly().AsResource(),
			new ReplMcpServerOptions { ResourceFallbackToTools = true });

		tools.Should().ContainSingle(t => string.Equals(t.ProtocolTool.Name, "contacts", StringComparison.Ordinal));
		resources.Should().ContainSingle();
	}

	// ── PromptFallbackToTools ──────────────────────────────────────────

	[TestMethod]
	[Description("Prompt-only commands are NOT tools by default.")]
	public void When_PromptOnly_Then_NotATool()
	{
		var (tools, _, prompts) = Generate(
			app => app.Map("troubleshoot {symptom}", (string symptom) => $"Diagnose: {symptom}").AsPrompt(),
			new ReplMcpServerOptions());

		tools.Should().NotContain(t => string.Equals(t.ProtocolTool.Name, "troubleshoot", StringComparison.Ordinal));
		prompts.Should().ContainSingle();
	}

	[TestMethod]
	[Description("Prompt-only commands become tools when PromptFallbackToTools is enabled.")]
	public void When_PromptFallbackEnabled_Then_PromptAlsoATool()
	{
		var (tools, _, prompts) = Generate(
			app => app.Map("troubleshoot {symptom}", (string symptom) => $"Diagnose: {symptom}").AsPrompt(),
			new ReplMcpServerOptions { PromptFallbackToTools = true });

		tools.Should().ContainSingle(t => string.Equals(t.ProtocolTool.Name, "troubleshoot", StringComparison.Ordinal));
		prompts.Should().ContainSingle();
	}

	// ── AutomationHidden ───────────────────────────────────────────────

	[TestMethod]
	[Description("AutomationHidden commands are never tools, resources, or prompts.")]
	public void When_AutomationHidden_Then_ExcludedFromEverything()
	{
		var (tools, _, prompts) = Generate(
			app => app.Map("wizard", () => "ok").AutomationHidden().AsResource().AsPrompt(),
			new ReplMcpServerOptions { ResourceFallbackToTools = true, PromptFallbackToTools = true });

		tools.Should().BeEmpty();
		// AutomationHidden commands are excluded from all MCP surfaces.
		prompts.Should().BeEmpty();
	}

	// ── Hidden/AutomationHidden filtering on resources and prompts ──────

	[TestMethod]
	[Description("Hidden resource commands are excluded from resources/list.")]
	public void When_HiddenResource_Then_ExcludedFromResources()
	{
		var (_, resources, _) = Generate(
			app => app.Map("secret-config", () => "ok").AsResource().Hidden(),
			new ReplMcpServerOptions());

		resources.Should().BeEmpty();
	}

	[TestMethod]
	[Description("AutomationHidden resource commands are excluded from resources/list.")]
	public void When_AutomationHiddenResource_Then_ExcludedFromResources()
	{
		var (_, resources, _) = Generate(
			app => app.Map("debug-state", () => "ok").AsResource().AutomationHidden(),
			new ReplMcpServerOptions());

		resources.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Hidden prompt commands are excluded from prompts/list.")]
	public void When_HiddenPrompt_Then_ExcludedFromPrompts()
	{
		var (_, _, prompts) = Generate(
			app => app.Map("internal-prompt {x}", (string x) => x).AsPrompt().Hidden(),
			new ReplMcpServerOptions());

		prompts.Should().BeEmpty();
	}

	[TestMethod]
	[Description("AutomationHidden prompt commands are excluded from prompts/list.")]
	public void When_AutomationHiddenPrompt_Then_ExcludedFromPrompts()
	{
		var (_, _, prompts) = Generate(
			app => app.Map("wizard-prompt {x}", (string x) => x).AsPrompt().AutomationHidden(),
			new ReplMcpServerOptions());

		prompts.Should().BeEmpty();
	}

	// ── Tool name collision detection ──────────────────────────────────

	[TestMethod]
	[Description("Different routes that flatten to the same tool name throw at startup.")]
	public void When_DifferentRoutesCollide_Then_ThrowsAtStartup()
	{
		var act = () => Generate(
			app =>
			{
				app.Map("contact add", () => "ok");
				app.Map("contact_add", () => "ok");
			},
			new ReplMcpServerOptions());

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*collision*");
	}

	[TestMethod]
	[Description("Same command in multiple phases (ReadOnly = core + resource fallback) does not throw.")]
	public void When_SameCommandInMultiplePhases_Then_NoDuplicate()
	{
		var (tools, resources, _) = Generate(
			app => app.Map("status", () => "ok").ReadOnly().AsResource(),
			new ReplMcpServerOptions { ResourceFallbackToTools = true });

		tools.Should().ContainSingle(t =>
			string.Equals(t.ProtocolTool.Name, "status", StringComparison.Ordinal));
		resources.Should().ContainSingle();
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static (
		List<McpServerTool> Tools,
		List<McpServerResource> Resources,
		List<McpServerPrompt> Prompts) Generate(
		Action<ReplApp> configure,
		ReplMcpServerOptions options)
	{
		var app = ReplApp.Create();
		configure(app);

		var handler = new McpServerHandler(app.Core, options, EmptyServiceProvider.Instance);
		var snapshot = handler.BuildSnapshotForTests();
		var tools = snapshot.Tools;
		var resources = snapshot.Resources;
		var prompts = snapshot.Prompts;

		return (tools, resources, prompts);
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
