using System.Text.Json;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Repl.Interaction;
using Repl.Mcp;

// These tests exercise Roots/Sampling/Logging, deprecated by MCP spec 2026-07-28
// (SEP-2577, MCP9005) but still supported by Repl.Mcp until the SDK removes them.
// Tracked in issue #51.
#pragma warning disable MCP9005

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
			await using var fixture = await CreateFeedbackFixtureAsync(LegacyClientOptions()).ConfigureAwait(false);

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
		var captureState = new NotificationCaptureState(notifications, progressUpdates);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateStructuredProgressFixtureAsync(LegacyClientOptions()).ConfigureAwait(false);

			var result = await fixture.Client.CallToolAsync(
				toolName: "feedback_progress",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal),
				progress: new Progress<ProgressNotificationValue>(_ => { })).ConfigureAwait(false);

			await WaitForConditionAsync(() =>
			{
				return HasExpectedProgressSequence(progressUpdates) && notifications.Count >= 2;
			}, timeoutMs: 5000).ConfigureAwait(false);
			AssertStructuredProgressResult(result, progressUpdates, notifications);
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	[TestMethod]
	[Description("Guards the 2026-07-28 rule that a server MUST NOT emit notifications/message for a request that declared no log level (SEP-2575) — and guards against that rule silently swallowing user feedback: the notice, warning and problem the command reported must instead ride back in the tool result, so no host loses them.")]
	public async Task When_RequestDeclaresNoLogLevel_Then_FeedbackRidesInTheToolResultInstead()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(CreateClientOptions()).ConfigureAwait(false);
			fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var result = await fixture.Client.CallToolAsync(
				toolName: "feedback",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			// Give a (forbidden) notification time to arrive before asserting that none did.
			await WaitForConditionAsync(() => notifications.Count > 0, timeoutMs: 500).ConfigureAwait(false);

			notifications.Should().BeEmpty(
				because: "the request declared no log level, so the server must not emit message notifications");
			var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
			text.Should().Contain("Connected");
			text.Should().Contain("Token expires soon");
			text.Should().Contain("Sync failed");
			result.IsError.Should().BeFalse();
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	[TestMethod]
	[Description("Guards severity filtering against the level the client asked for: after logging/setLevel(Error) the notice and warning a command reports must not be delivered, while the problem must. Emitting everything regardless of the requested threshold floods hosts that deliberately asked for errors only.")]
	public async Task When_ClientRequestsErrorLevel_Then_LowerSeveritiesAreNotNotified()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(LegacyClientOptions()).ConfigureAwait(false);
			await fixture.Client.SetLoggingLevelAsync(LoggingLevel.Error).ConfigureAwait(false);

			await fixture.Client.CallToolAsync(
				toolName: "feedback",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			await WaitForConditionAsync(() => notifications.Count >= 1).ConfigureAwait(false);

			notifications.Should().OnlyContain(entry => entry.Level == LoggingLevel.Error);
			notifications.Should().ContainSingle(entry =>
				entry.Data.Contains("Sync failed", StringComparison.Ordinal));
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	/// <summary>
	/// A client pinned to the last revision on which message notifications can be requested at all.
	/// </summary>
	/// <remarks>
	/// On <c>2026-07-28</c> the SDK's client cannot ask for a log level: it rejects
	/// <c>logging/setLevel</c> for that revision, exposes no option for the level, and replaces a
	/// caller's <c>_meta</c> with its own keys (protocol version, client info, capabilities). Tests
	/// that assert notification DELIVERY therefore have to pin the initialize-era revision. The modern
	/// path is covered by
	/// <see cref="When_RequestDeclaresNoLogLevel_Then_FeedbackRidesInTheToolResultInstead"/>.
	/// </remarks>
	private static McpClientOptions LegacyClientOptions()
	{
		var options = CreateClientOptions();
		options.ProtocolVersion = McpProtocolRevisions.LastWithSessions;
		return options;
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
					new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
						NotificationMethods.ProgressNotification,
						HandleProgressNotificationAsync),
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

	private static ValueTask HandleProgressNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var state = NotificationCaptureState.Current
			?? throw new InvalidOperationException("Notification capture state was not initialized.");
		if (state.ProgressUpdates is null)
		{
			return ValueTask.CompletedTask;
		}

		var payload = notification.Params?.Deserialize<ProgressNotificationParams>()
			?? throw new InvalidOperationException("Expected progress notification parameters.");
		lock (state.ProgressUpdates)
		{
			state.ProgressUpdates.Add(payload.Progress);
		}

		return ValueTask.CompletedTask;
	}

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
		var progressDump = DescribeProgressUpdates(progressUpdates);
		var notificationDump = DescribeNotifications(notifications);

		result.IsError.Should().BeFalse();
		if (!HasExpectedProgressSequence(progressUpdates))
		{
			Assert.Fail($"Unexpected progress updates: {progressDump}");
		}
		notifications.Should().Contain(entry =>
			entry.Level == LoggingLevel.Warning
			&& entry.Data.Contains("\"state\":\"Warning\"", StringComparison.Ordinal)
			&& entry.Data.Contains("\"details\":\"Transient issue\"", StringComparison.Ordinal), $"notifications were: {notificationDump}");
		notifications.Should().Contain(entry =>
			entry.Level == LoggingLevel.Error
			&& entry.Data.Contains("\"state\":\"Error\"", StringComparison.Ordinal)
			&& entry.Data.Contains("\"details\":\"Permanent issue\"", StringComparison.Ordinal), $"notifications were: {notificationDump}");
	}

	private static bool HasExpectedProgressSequence(List<ProgressNotificationValue> progressUpdates) =>
		ContainsProgressUpdate(progressUpdates, 25f, 100f, "Loading")
		&& ContainsProgressUpdate(progressUpdates, 0f, expectedTotal: null, "Waiting: Remote side")
		&& ContainsProgressUpdate(progressUpdates, 60f, 100f, "Retrying: Transient issue")
		&& ContainsProgressUpdate(progressUpdates, 80f, 100f, "Failed: Permanent issue");

	private static bool ContainsProgressUpdate(
		List<ProgressNotificationValue> progressUpdates,
		float expectedProgress,
		float? expectedTotal,
		string expectedMessage)
	{
		const float epsilon = 0.0001f;

		return progressUpdates.Exists(update =>
			NearlyEqual(update.Progress, expectedProgress, epsilon)
			&& TotalsMatch(update.Total, expectedTotal, epsilon)
			&& string.Equals(update.Message, expectedMessage, StringComparison.Ordinal));
	}

	private static bool TotalsMatch(float? actual, float? expected, float epsilon) =>
		expected is null
			? actual is null
			: actual is float total && NearlyEqual(total, expected.Value, epsilon);

	private static bool NearlyEqual(float actual, float expected, float epsilon) =>
		MathF.Abs(actual - expected) <= epsilon;

	private static string DescribeProgressUpdates(List<ProgressNotificationValue> progressUpdates) =>
		progressUpdates.Count == 0
			? "<none>"
			: string.Join(
				" | ",
				progressUpdates.Select(update =>
					$"Progress={update.Progress.ToString(CultureInfo.InvariantCulture)}, Total={(update.Total?.ToString(CultureInfo.InvariantCulture) ?? "null")}, Message={update.Message ?? "<null>"}"));

	private static string DescribeNotifications(List<(LoggingLevel Level, string Data)> notifications) =>
		notifications.Count == 0
			? "<none>"
			: string.Join(" | ", notifications.Select(entry => $"{entry.Level}: {entry.Data}"));

	private sealed record NotificationCaptureState(
		List<(LoggingLevel Level, string Data)> Notifications,
		List<ProgressNotificationValue>? ProgressUpdates = null)
	{
		public static NotificationCaptureState? Current { get; set; }
	}
}
