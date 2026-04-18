using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Interaction;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpUserFeedback
{
	[TestMethod]
	[Description("Interaction-based user feedback is routed as MCP logging notifications with the expected severities.")]
	public async Task When_ToolEmitsUserFeedback_Then_McpReceivesNotifications()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(clientOptions: CreateClientOptions()).ConfigureAwait(false);

			var result = await fixture.Client.CallToolAsync(
				toolName: "feedback",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			await WaitForConditionAsync(() => notifications.Count >= 3).ConfigureAwait(false);
			AssertFeedbackResult(result: result, notifications: notifications);
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	[TestMethod]
	[Description("Structured progress feedback is routed through MCP progress notifications and warning/error logging notifications.")]
	public async Task When_ToolEmitsStructuredProgress_Then_McpReceivesProgressAndMessages()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var progressUpdates = new List<ProgressNotificationValue>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateStructuredProgressFixtureAsync(CreateClientOptions()).ConfigureAwait(false);
			var progressHandler = CreateProgressCollector(progressUpdates);

			var result = await fixture.Client.CallToolAsync(
				toolName: "feedback_progress",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
				progress: progressHandler).ConfigureAwait(false);

			await WaitForConditionAsync(() => progressUpdates.Count >= 4 && notifications.Count >= 2).ConfigureAwait(false);
			AssertStructuredProgressResult(result, progressUpdates, notifications);
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	private static async Task<McpTestFixture> CreateFeedbackFixtureAsync(
		McpClientOptions clientOptions) =>
		await CreateFeedbackFixtureAsync(
			app =>
			{
				app.Map("feedback", static async (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
				{
					await interaction.WriteNoticeAsync(text: "Connected", cancellationToken: cancellationToken).ConfigureAwait(false);
					await interaction.WriteWarningAsync(text: "Token expires soon", cancellationToken: cancellationToken).ConfigureAwait(false);
					await interaction.WriteProblemAsync(
						summary: "Sync failed",
						details: "Retry later.",
						code: "sync_failed",
						cancellationToken: cancellationToken).ConfigureAwait(false);
					return "done";
				});
			},
			clientOptions).ConfigureAwait(false);

	private static async Task<McpTestFixture> CreateFeedbackFixtureAsync(
		Action<ReplApp> configure,
		McpClientOptions clientOptions) =>
		await McpTestFixture.CreateAsync(
			configure,
			configureOptions: null,
			clientOptions: clientOptions).ConfigureAwait(false);

	private static Task<McpTestFixture> CreateStructuredProgressFixtureAsync(McpClientOptions clientOptions) =>
		CreateFeedbackFixtureAsync(
			app =>
			{
				app.Map("feedback progress", static async (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
				{
					await interaction.WriteProgressAsync(
						new ReplProgressEvent("Loading", Percent: 25),
						cancellationToken).ConfigureAwait(false);
					await interaction.WriteIndeterminateProgressAsync(
						label: "Waiting",
						details: "Remote side",
						cancellationToken: cancellationToken).ConfigureAwait(false);
					await interaction.WriteWarningProgressAsync(
						label: "Retrying",
						percent: 60,
						details: "Transient issue",
						cancellationToken: cancellationToken).ConfigureAwait(false);
					await interaction.WriteErrorProgressAsync(
						label: "Failed",
						percent: 80,
						details: "Permanent issue",
						cancellationToken: cancellationToken).ConfigureAwait(false);
					return "done";
				});
			},
			clientOptions);

	private static McpClientOptions CreateClientOptions() =>
		new()
		{
			Handlers = new McpClientHandlers
			{
				NotificationHandlers =
				[
					new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
						NotificationMethods.LoggingMessageNotification,
						HandleLoggingNotificationAsync),
				],
			},
		};

	private static ValueTask HandleLoggingNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var state = NotificationCaptureState.Current
			?? throw new InvalidOperationException("Notification capture state was not initialized.");
		var payload = notification.Params?.Deserialize<LoggingMessageNotificationParams>()
			?? throw new InvalidOperationException("Expected logging notification parameters.");
		lock (state.Notifications)
		{
			state.Notifications.Add((
				payload.Level,
				payload.Data.ValueKind == JsonValueKind.String
					? payload.Data.GetString() ?? string.Empty
					: payload.Data.GetRawText()));
		}

		return ValueTask.CompletedTask;
	}

	private static Progress<ProgressNotificationValue> CreateProgressCollector(List<ProgressNotificationValue> progressUpdates) =>
		new Progress<ProgressNotificationValue>(value =>
		{
			lock (progressUpdates)
			{
				progressUpdates.Add(value);
			}
		});

	private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 1000)
	{
		var started = Environment.TickCount64;
		while (!predicate())
		{
			if (Environment.TickCount64 - started > timeoutMs)
			{
				break;
			}

			await Task.Delay(25).ConfigureAwait(false);
		}
	}

	private static void AssertFeedbackResult(CallToolResult result, List<(LoggingLevel Level, string Data)> notifications)
	{
		result.IsError.Should().BeFalse();
		notifications.Exists(entry => entry.Level == LoggingLevel.Info && entry.Data.Contains("Connected", StringComparison.Ordinal))
			.Should().BeTrue();
		notifications.Exists(entry => entry.Level == LoggingLevel.Warning && entry.Data.Contains("Token expires soon", StringComparison.Ordinal))
			.Should().BeTrue();
		notifications.Exists(entry => entry.Level == LoggingLevel.Error && entry.Data.Contains("\"summary\":\"Sync failed\"", StringComparison.Ordinal))
			.Should().BeTrue();
	}

	private static void AssertStructuredProgressResult(
		CallToolResult result,
		List<ProgressNotificationValue> progressUpdates,
		List<(LoggingLevel Level, string Data)> notifications)
	{
		result.IsError.Should().BeFalse();
		progressUpdates.Exists(update =>
			update.Progress == 25f
			&& update.Total == 100f
			&& string.Equals(update.Message, "Loading", StringComparison.Ordinal)).Should().BeTrue();
		progressUpdates.Exists(update =>
			update.Progress == 0f
			&& update.Total == null
			&& string.Equals(update.Message, "Waiting: Remote side", StringComparison.Ordinal)).Should().BeTrue();
		progressUpdates.Exists(update =>
			update.Progress == 60f
			&& update.Total == 100f
			&& string.Equals(update.Message, "Retrying: Transient issue", StringComparison.Ordinal)).Should().BeTrue();
		progressUpdates.Exists(update =>
			update.Progress == 80f
			&& update.Total == 100f
			&& string.Equals(update.Message, "Failed: Permanent issue", StringComparison.Ordinal)).Should().BeTrue();
		notifications.Should().Contain(entry =>
			entry.Level == LoggingLevel.Warning
			&& entry.Data.Contains("\"state\":\"Warning\"", StringComparison.Ordinal)
			&& entry.Data.Contains("\"details\":\"Transient issue\"", StringComparison.Ordinal));
		notifications.Should().Contain(entry =>
			entry.Level == LoggingLevel.Error
			&& entry.Data.Contains("\"state\":\"Error\"", StringComparison.Ordinal)
			&& entry.Data.Contains("\"details\":\"Permanent issue\"", StringComparison.Ordinal));
	}

	private sealed record NotificationCaptureState(List<(LoggingLevel Level, string Data)> Notifications)
	{
		public static NotificationCaptureState? Current { get; set; }
	}
}
