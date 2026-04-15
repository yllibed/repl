using System.Text.Json;
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

			AssertFeedbackResult(result: result, notifications: notifications);
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	private static async Task<McpTestFixture> CreateFeedbackFixtureAsync(McpClientOptions clientOptions) =>
		await McpTestFixture.CreateAsync(
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
			configureOptions: null,
			clientOptions: clientOptions).ConfigureAwait(false);

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

	private static void AssertFeedbackResult(CallToolResult result, List<(LoggingLevel Level, string Data)> notifications)
	{
		result.IsError.Should().BeFalse();
		notifications.Should().Contain(entry => entry.Level == LoggingLevel.Info && string.Equals(entry.Data, "Connected", StringComparison.Ordinal));
		notifications.Should().Contain(entry => entry.Level == LoggingLevel.Warning && string.Equals(entry.Data, "Token expires soon", StringComparison.Ordinal));
		notifications.Should().Contain(entry => entry.Level == LoggingLevel.Error && entry.Data.Contains("\"summary\":\"Sync failed\"", StringComparison.Ordinal));
	}

	private sealed record NotificationCaptureState(List<(LoggingLevel Level, string Data)> Notifications)
	{
		public static NotificationCaptureState? Current { get; set; }
	}
}
