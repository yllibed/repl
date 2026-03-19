using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpConcurrentSessions
{
	[TestMethod]
	[Description("Two independent MCP sessions can run concurrently without interference.")]
	public async Task When_TwoSessionsRunConcurrently_Then_EachSeesOwnTools()
	{
		var session1 = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("alpha", () => "a");
		}).ConfigureAwait(false);

		await using (session1.ConfigureAwait(false))
		{
			var session2 = await McpTestFixture.CreateAsync(app =>
			{
				app.Map("beta", () => "b");
				app.Map("gamma", () => "c");
			}).ConfigureAwait(false);

			await using (session2.ConfigureAwait(false))
			{
				var tools1 = await session1.Client.ListToolsAsync().ConfigureAwait(false);
				var tools2 = await session2.Client.ListToolsAsync().ConfigureAwait(false);

				tools1.Should().ContainSingle(t => string.Equals(t.Name, "alpha", StringComparison.Ordinal));
				tools1.Should().NotContain(t => string.Equals(t.Name, "beta", StringComparison.Ordinal));

				tools2.Should().NotContain(t => string.Equals(t.Name, "alpha", StringComparison.Ordinal));
				tools2.Should().HaveCount(2);
			}
		}
	}

	[TestMethod]
	[Description("Concurrent tool invocations on separate sessions do not cross-contaminate output.")]
	public async Task When_ToolsInvokedConcurrently_Then_OutputIsIsolated()
	{
		var session1 = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("echo {msg}", (string msg) => $"s1:{msg}");
		}).ConfigureAwait(false);

		await using (session1.ConfigureAwait(false))
		{
			var session2 = await McpTestFixture.CreateAsync(app =>
			{
				app.Map("echo {msg}", (string msg) => $"s2:{msg}");
			}).ConfigureAwait(false);

			await using (session2.ConfigureAwait(false))
			{
				var result1 = await session1.Client.CallToolAsync(
					"echo", new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = "hello" })
					.ConfigureAwait(false);
				var result2 = await session2.Client.CallToolAsync(
					"echo", new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = "hello" })
					.ConfigureAwait(false);

				var text1 = result1.Content.OfType<TextContentBlock>().First().Text;
				var text2 = result2.Content.OfType<TextContentBlock>().First().Text;

				text1.Should().Contain("s1:hello");
				text2.Should().Contain("s2:hello");
			}
		}
	}
}
