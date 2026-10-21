using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpExitCodePolicy
{
	[TestMethod]
	[Description("Regression guard: verifies neither the exit-code table nor the resolver applies to MCP sub-invocations so that a process-level remap cannot hide a failed tool call from the agent.")]
	public async Task When_ExitCodePolicyMapsEverythingToZero_Then_ToolCallStillReportsError()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Options(options =>
			{
				options.ExitCodes.HandlerError = 0;
				options.ExitCodes.Resolver = static _ => 0;
			});
			app.Map("boom", () => Results.Error("boom", "nope"))
				.ReadOnly();
		}).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync(
			toolName: "boom",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		result.IsError.Should().BeTrue();
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text))
			.Should().Contain("nope");
	}

	[TestMethod]
	[Description("Regression guard: verifies the codes a module declares do not apply to MCP sub-invocations either, so a module mapping success to a non-zero code, or a failure to zero, cannot turn its tool calls into errors or hide a failed one.")]
	public async Task When_AModuleDeclaresItsCodes_Then_ToolCallsKeepTheBuiltInVerdict()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.MapModule(new RemappingModule())).ConfigureAwait(false);

		var succeeded = await fixture.Client.CallToolAsync(
			toolName: "succeed",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);
		var failed = await fixture.Client.CallToolAsync(
			toolName: "fail",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		(succeeded.IsError ?? false).Should().BeFalse();
		failed.IsError.Should().BeTrue();
	}

	private sealed class RemappingModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { Success = 10, HandlerError = 0 };

		public void Map(IReplMap map)
		{
			map.Map("succeed", () => "ok").ReadOnly();
			map.Map("fail", () => Results.Error("boom", "nope")).ReadOnly();
		}
	}
}
