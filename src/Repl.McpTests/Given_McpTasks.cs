using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpTasks
{
	[TestMethod]
	[Description("A .LongRunning() Repl command uses the official Tasks extension: the initial call creates a task and polling returns its completed tool result.")]
	public async Task When_LongRunningCommandIsCalledByModernClient_Then_ResultIsAvailableThroughTaskPolling()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("deploy", async () =>
			{
				await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
				return "deployed";
			}).LongRunning();
		});

		_ = await fixture.Client.ListToolsAsync();
		var started = await fixture.Client.CallToolAsTaskAsync(
			new CallToolRequestParams { Name = "deploy" });

		started.IsTask.Should().BeTrue();
		started.TaskCreated.Should().NotBeNull();
		started.TaskCreated!.Status.Should().Be(McpTaskStatus.Working);

		var completed = await WaitForCompletionAsync(fixture.Client, started.TaskCreated.TaskId);
		ReadJsonString(completed.Result.GetProperty("content")[0].GetProperty("text").GetString()).Should().Be("deployed");
	}

	[TestMethod]
	[Description("A modern Tasks-capable client still receives a normal immediate response for a Repl command not marked .LongRunning().")]
	public async Task When_RegularCommandIsCalledByModernClient_Then_ItRemainsSynchronous()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map("status", () => "ready"));

		_ = await fixture.Client.ListToolsAsync();
		var invocation = await fixture.Client.CallToolAsTaskAsync(
			new CallToolRequestParams { Name = "status" });

		invocation.IsTask.Should().BeFalse();
		invocation.Result!.Content.Should().ContainSingle();
		ReadJsonString(((TextContentBlock)invocation.Result.Content[0]).Text).Should().Be("ready");
	}

	[TestMethod]
	[Description("A legacy 2025-11-25 client invokes a long-running Repl command synchronously and is never given a modern task handle.")]
	public async Task When_LongRunningCommandIsCalledByLegacyClient_Then_ItFallsBackToSynchronousInvocation()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("deploy", () => "deployed").LongRunning(),
			configureOptions: null,
			clientOptions: new McpClientOptions { ProtocolVersion = "2025-11-25" });

		var result = await fixture.Client.CallToolAsync(new CallToolRequestParams { Name = "deploy" });

		result.Content.Should().ContainSingle();
		ReadJsonString(((TextContentBlock)result.Content[0]).Text).Should().Be("deployed");
	}

	[TestMethod]
	[Description("Cancelling a running Repl task transitions it to cancelled and forwards cancellation to the command.")]
	public async Task When_ModernClientCancelsLongRunningCommand_Then_TaskIsCancelled()
	{
		var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var fixture = await McpTestFixture.CreateAsync(app =>
		{
			app.Map("wait", async (CancellationToken cancellationToken) =>
			{
				commandStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
				return "unreachable";
			}).LongRunning();
		});

		_ = await fixture.Client.ListToolsAsync();
		var started = await fixture.Client.CallToolAsTaskAsync(
			new CallToolRequestParams { Name = "wait" });
		started.IsTask.Should().BeTrue();

		await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await fixture.Client.CancelTaskAsync(started.TaskCreated!.TaskId);

		var task = await fixture.Client.GetTaskAsync(started.TaskCreated.TaskId);
		task.Should().BeOfType<CancelledTaskResult>();
	}

	private static async Task<CompletedTaskResult> WaitForCompletionAsync(McpClient client, string taskId)
	{
		for (var attempt = 0; attempt < 40; attempt++)
		{
			var task = await client.GetTaskAsync(taskId).ConfigureAwait(false);
			if (task is CompletedTaskResult completed)
			{
				return completed;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
		}

		throw new TimeoutException($"Task '{taskId}' did not complete within one second.");
	}

	private static string? ReadJsonString(string? json)
	{
		using var document = JsonDocument.Parse(json ?? "null");
		return document.RootElement.GetString();
	}
}
