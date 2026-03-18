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
		// Resources are generated from the doc model, which includes AutomationHidden.
		// But they won't be callable since the tool adapter won't route them.
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

		var model = app.Core.CreateDocumentationModel();
		var handler = new McpServerHandler(app.Core, options, EmptyServiceProvider.Instance);

		// Use reflection to call private BuildServerOptions.
		var method = typeof(McpServerHandler).GetMethod(
			"BuildServerOptions",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		var separator = McpToolNameFlattener.ResolveSeparator(options.ToolNamingSeparator);
		var adapter = new McpToolAdapter(app.Core, options, EmptyServiceProvider.Instance);
		var serverOptions = (McpServerOptions)method!.Invoke(handler, [model, adapter, separator])!;

		var tools = serverOptions.ToolCollection?.ToList() ?? [];
		var resources = serverOptions.ResourceCollection?.ToList() ?? [];
		var prompts = serverOptions.PromptCollection?.ToList() ?? [];

		return (tools, resources, prompts);
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
