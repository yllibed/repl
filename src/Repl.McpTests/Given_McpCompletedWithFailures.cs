using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpCompletedWithFailures
{
	[TestMethod]
	[Description("Verifies a tool that completed with failures is not reported as a tool error: the agent receives the report itself, and a marker that says the run found failures.")]
	public async Task When_ToolCompletesWithFailures_Then_ResultIsNotAnErrorAndCarriesThePayload()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("tests run", () => Results.CompletedWithFailures(new { Passed = 3, Failed = 1 }))
				.ReadOnly()).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync(
			toolName: "tests_run",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		(result.IsError ?? false).Should().BeFalse();
		using (var payload = JsonDocument.Parse(TextOf(result)))
		{
			payload.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
		}

		result.Meta.Should().NotBeNull();
		var meta = result.Meta!;
		meta["io.github.yllibed.repl/outcome"]!.GetValue<string>().Should().Be("completedWithFailures");
	}

	[TestMethod]
	[Description("Regression guard: verifies a handler that throws is still a tool error, so completed-with-failures stays distinguishable from a crash.")]
	public async Task When_ToolThrows_Then_ResultIsAnErrorWithoutTheOutcomeMarker()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("tests run", (Func<string>)(() => throw new InvalidOperationException("boom")))
				.ReadOnly()).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync(
			toolName: "tests_run",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		result.IsError.Should().BeTrue();
		TextOf(result).Should().NotContain("boom");
		var hasOutcomeMarker = result.Meta?.ContainsKey("io.github.yllibed.repl/outcome") ?? false;
		hasOutcomeMarker.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies a run that completed with failures but rendered no report does not read as a success to an agent that only reads the text.")]
	public async Task When_ToolCompletesWithFailuresWithoutPayload_Then_TheTextSaysFailuresWereFound()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("tests run", () => Results.CompletedWithFailures())
				.ReadOnly()).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync(
			toolName: "tests_run",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		(result.IsError ?? false).Should().BeFalse();
		TextOf(result).Should().Be("Completed with failures.");
	}

	[TestMethod]
	[Description("Verifies a paged report that completed with failures keeps its structured content and carries the outcome marker too.")]
	public async Task When_PagedToolCompletesWithFailures_Then_StructuredContentAndMarkerArePresent()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("tests run", (IReplPagingContext paging) =>
					Results.CompletedWithFailures(paging.Page(FailedTests, nextCursor: "page-2")))
				.ReadOnly()).ConfigureAwait(false);

		var result = await fixture.Client.CallToolAsync(
			toolName: "tests_run",
			arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

		(result.IsError ?? false).Should().BeFalse();
		result.StructuredContent.Should().NotBeNull();
		result.Meta.Should().NotBeNull();
		result.Meta!["io.github.yllibed.repl/outcome"]!.GetValue<string>().Should().Be("completedWithFailures");
	}

	private static readonly string[] FailedTests = ["failed-test"];

	private static string TextOf(CallToolResult result) =>
		string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
}
