using Repl.Parameters;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
	private const int RawRequestId = 1;

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
			result.Content[0].Should().BeOfType<TextContentBlock>(
				because: "the documented guarantee is about Content[0], not about the first text block");
			var blocks = result.Content.OfType<TextContentBlock>().ToArray();
			blocks[0].Text.Should().Contain(
				"done",
				because: "the command's own payload stays the first block, which is what makes the "
					+ "appended messages non-breaking for a client reading Content[0]");
			blocks[0].Text.Should().NotContain("Connected", because: "messages are appended, never prepended");

			var text = string.Join('\n', blocks.Select(block => block.Text));
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
	[Description("Guards the half of the 2026-07-28 logging rule that had no coverage at all: a request that DOES declare _meta/io.modelcontextprotocol/logLevel must receive notifications, filtered at that level. The SDK's own client cannot express this - it replaces a caller's _meta with its own keys - so the request goes out as a raw JSON-RPC frame. Without it, a regression resolving no threshold on the modern path would disable the feature silently and leave every other test green.")]
	public async Task When_ARequestDeclaresALogLevel_Then_MessagesAtOrAboveItAreNotified()
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("feedback", static async (IMcpFeedback feedback, CancellationToken ct) =>
		{
			await feedback.SendMessageAsync(McpMessageLevel.Info, "below-threshold", ct).ConfigureAwait(false);
			await feedback.SendMessageAsync(McpMessageLevel.Error, "above-threshold", ct).ConfigureAwait(false);
			return "done";
		});

		// Assembled as a node rather than written as a literal: the whole point of this test is the
		// _meta the SDK client overwrites, and hand-escaped JSON is how a typo becomes a silent pass.
		var request = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = RawRequestId,
			["method"] = "tools/call",
			["params"] = new JsonObject
			{
				["name"] = "feedback",
				["arguments"] = new JsonObject(),
				["_meta"] = new JsonObject
				{
					["io.modelcontextprotocol/protocolVersion"] = McpProtocolRevisions.Sessionless,
					// Required by the revision: the server rejects a request without it.
					["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
					["io.modelcontextprotocol/logLevel"] = "warning",
				},
			},
		};

		var frames = await ExchangeRawFrameAsync(app.BuildMcpServerOptions(), request).ConfigureAwait(false);

		var notified = frames
			.Where(static frame => string.Equals(
				frame["method"]?.GetValue<string>(),
				NotificationMethods.LoggingMessageNotification,
				StringComparison.Ordinal))
			.Select(static frame => frame["params"]?["data"]?.GetValue<string>())
			.ToArray();

		notified.Should().Contain(
			"above-threshold",
			because: "a request that declared a level must receive messages at or above it");
		notified.Should().NotContain(
			"below-threshold",
			because: "the declared level is a threshold, not merely permission to send");
	}

	/// <summary>
	/// Hosts <paramref name="mcpOptions"/> on a pipe pair, writes one raw JSON-RPC frame, and returns
	/// everything the server wrote up to and including its response.
	/// </summary>
	private static async Task<IReadOnlyList<JsonObject>> ExchangeRawFrameAsync(
		McpServerOptions mcpOptions,
		JsonObject request)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var transport = new StreamServerTransport(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream(),
			serverName: "raw-meta-server");
		var server = McpServer.Create(transport, mcpOptions);
		var serverTask = server.RunAsync(cts.Token);
		try
		{
			var payload = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
			await clientToServer.Writer.WriteAsync(payload, cts.Token).ConfigureAwait(false);
			await clientToServer.Writer.FlushAsync(cts.Token).ConfigureAwait(false);

			return await ReadFramesUntilResponseAsync(serverToClient.Reader, cts.Token).ConfigureAwait(false);
		}
		finally
		{
			await cts.CancelAsync().ConfigureAwait(false);
			await clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
			await serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
			try
			{
				await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Expected: RunAsync ends on cancellation.
			}

			await server.DisposeAsync().ConfigureAwait(false);
			await transport.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static async Task<IReadOnlyList<JsonObject>> ReadFramesUntilResponseAsync(
		PipeReader reader,
		CancellationToken cancellationToken)
	{
		var frames = new List<JsonObject>();
		using var lines = new StreamReader(reader.AsStream(), Encoding.UTF8);
		while (await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			if (JsonNode.Parse(line) is not JsonObject frame)
			{
				continue;
			}

			frames.Add(frame);
			if (frame["id"] is JsonValue id && id.TryGetValue(out int value) && value == RawRequestId)
			{
				break;
			}
		}

		return frames;
	}

	[TestMethod]
	[DataRow(McpMessageLevel.Debug, LoggingLevel.Debug, DisplayName = "Debug")]
	[DataRow(McpMessageLevel.Info, LoggingLevel.Info, DisplayName = "Info")]
	[DataRow(McpMessageLevel.Notice, LoggingLevel.Notice, DisplayName = "Notice")]
	[DataRow(McpMessageLevel.Warning, LoggingLevel.Warning, DisplayName = "Warning")]
	[DataRow(McpMessageLevel.Error, LoggingLevel.Error, DisplayName = "Error")]
	[DataRow(McpMessageLevel.Critical, LoggingLevel.Critical, DisplayName = "Critical")]
	[DataRow(McpMessageLevel.Alert, LoggingLevel.Alert, DisplayName = "Alert")]
	[DataRow(McpMessageLevel.Emergency, LoggingLevel.Emergency, DisplayName = "Emergency")]
	[Description("Guards the equivalence the upgrade note in docs/mcp-reference.md promises consumers: McpMessageLevel has the same members and the same numeric values as the SDK's LoggingLevel, so swapping one for the other in a call is mechanical. Repl no longer leans on that agreement internally - the conversion is a switch - but the documentation still tells consumers it holds, and a silent SDK renumbering would make that advice wrong.")]
	public void When_AMessageLevelIsComparedToTheProtocolLevel_Then_NameAndValueAgree(
		McpMessageLevel level,
		LoggingLevel protocolLevel)
	{
		((int)level).Should().Be((int)protocolLevel);
		level.ToString().Should().Be(protocolLevel.ToString());
	}

	[TestMethod]
	[Description("Guards the other half of the same promise: the upgrade note tells consumers the two enums have the same MEMBERS, which eight known pairs cannot pin — an SDK addition would leave them all green while making the advice wrong, and would also reach FromProtocol's throw at runtime.")]
	public void When_TheProtocolLevelsAreEnumerated_Then_TheyMatchMcpMessageLevel()
	{
		Enum.GetNames<LoggingLevel>().Should().BeEquivalentTo(Enum.GetNames<McpMessageLevel>());
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

	[TestMethod]
	[Description("Regression guard: a prompt must carry the same buffered feedback a tool does. On 2026-07-28 a request that declared no log level receives no message notifications, so feedback the command reported survives only inside the result — and prompts/get kept just the first content block, making it the one path that silently discarded it.")]
	public async Task When_APromptDeclaresNoLogLevel_Then_FeedbackRidesInThePromptResultInstead()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(
				app => app.Map(
					"review",
					static async (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
					{
						await interaction.WriteNoticeAsync(
							text: "review-notice",
							cancellationToken: cancellationToken).ConfigureAwait(false);
						return "payload";
					}).AsPrompt(),
				CreateClientOptions()).ConfigureAwait(false);

			fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var result = await fixture.Client.GetPromptAsync("review").ConfigureAwait(false);
			var texts = result.Messages
				.Select(message => message.Content)
				.OfType<TextContentBlock>()
				.Select(block => block.Text)
				.ToArray();

			notifications.Should().BeEmpty(
				because: "the request declared no log level, so the server must not emit message notifications");
			texts[0].Should().Contain(
				"payload",
				because: "the command's own payload stays the first message, as it stays the first block on the tool path");
			string.Join(' ', texts).Should().Contain(
				"review-notice",
				because: "feedback the client could not receive as a notification must survive somewhere");
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	[TestMethod]
	[Description("Regression guard: a prompt that FAILS must still carry its buffered feedback. The error branch surfaces an McpException built from the content blocks, and building it from the first block alone discarded exactly the messages the no-log-level path exists to preserve — at the moment they matter most, since a notice explaining what went wrong is worth more on a failure than on a success.")]
	public async Task When_AFailingPromptDeclaresNoLogLevel_Then_FeedbackRidesInTheError()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(
				app => app.Map(
					"review",
					static async Task<string> (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
					{
						await interaction.WriteNoticeAsync(
							text: "review-notice",
							cancellationToken: cancellationToken).ConfigureAwait(false);
						throw new InvalidOperationException("review-failed");
					}).AsPrompt(),
				CreateClientOptions()).ConfigureAwait(false);

			fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var act = async () => await fixture.Client.GetPromptAsync("review").ConfigureAwait(false);

			(await act.Should().ThrowAsync<Exception>().ConfigureAwait(false))
				.Which.Message.Should().Contain(
					"review-notice",
					because: "feedback the client could not receive as a notification must survive a failure too");
		}
		finally
		{
			NotificationCaptureState.Current = null;
		}
	}

	[TestMethod]
	[Description("Regression guard: an ordinary resource read that fails must carry the feedback its command emitted. On success a resource body has to match the advertised MIME type, so trailing blocks are dropped there on purpose — but a failure has no body at all, and the resource path built its error from the command output alone, discarding the notices that explain it.")]
	public async Task When_AFailingResourceReadDeclaresNoLogLevel_Then_FeedbackRidesInTheError()
	{
		var notifications = new List<(LoggingLevel Level, string Data)>();
		var captureState = new NotificationCaptureState(notifications);
		NotificationCaptureState.Current = captureState;
		try
		{
			await using var fixture = await CreateFeedbackFixtureAsync(
				app => app.Map(
					"report",
					static async Task<string> (IReplInteractionChannel interaction, CancellationToken cancellationToken) =>
					{
						await interaction.WriteNoticeAsync(
							text: "report-notice",
							cancellationToken: cancellationToken).ConfigureAwait(false);
						throw new InvalidOperationException("report-failed");
					}).ReadOnly().AsResource(),
				CreateClientOptions()).ConfigureAwait(false);

			fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var resources = await fixture.Client.ListResourcesAsync().ConfigureAwait(false);
			var uri = resources.Select(static resource => resource.Uri).Single();

			var act = async () => await fixture.Client.ReadResourceAsync(uri).ConfigureAwait(false);

			(await act.Should().ThrowAsync<Exception>().ConfigureAwait(false))
				.Which.Message.Should().Contain(
					"report-notice",
					because: "a failed read has nowhere else to carry what the command reported");
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

	[TestMethod]
	[Description("Regression guard: a dependency that throws while being activated must not reach the client. Its exception escaped application code during binding \u2014 it can name a path, a connection string or provider internals \u2014 and it is classified as a binding failure, the same as a diagnostic the binder wrote itself, so the kind alone cannot tell them apart. The binder marks activation failures for exactly this.")]
	public async Task When_ADependencyFactoryThrows_Then_ItsMessageIsWithheld()
	{
		var session = await McpTestFixture.CreateAsync(
			configure: app => app.Map("probe", (IThrowingDependency dependency) => dependency.ToString() ?? "ok"),
			configureOptions: null,
			clientOptions: null,
			configureServices: services => services.AddSingleton<IThrowingDependency>(
				implementationFactory: static _ => throw new IOException("review-marker-detail"))).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "probe",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			result.IsError.Should().BeTrue();
			text.Should().NotContain(
				"review-marker-detail",
				because: "what a container factory raises is not the framework speaking to this caller");
			text.Should().NotContain(
				"IThrowingDependency",
				because: "the wrapper names the service and the parameter, which is the second half of "
					+ "what this withholds — the binder marks the failure, and MCP replaces even that");
		}
	}

	[TestMethod]
	[Description("Pins the other side: a diagnostic the binder wrote itself must still reach the client. It names what could not be supplied and is what an agent reads to correct its next call \u2014 withholding every binding failure to close the activation leak would trade a disclosure for silence on a far busier path.")]
	public async Task When_ARequiredDependencyIsMissing_Then_TheBindersDiagnosticReaches()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("probe", ([FromServices] IThrowingDependency dependency) => "ok")).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "probe",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			result.IsError.Should().BeTrue();
			text.Should().Contain(
				"dependency",
				because: "the binder named the parameter it could not supply, and that is actionable");
		}
	}

	[TestMethod]
	[Description("Regression guard: the interaction channel raises McpInteractionException to tell the caller which answer it failed to supply, naming the tool argument to send next time. That is addressed to the client, so the withholding rule must not swallow it — doing so would leave the default PrefillThenFail mode unable to say what it needs, and the caller with an exit code and no way forward.")]
	public async Task When_APromptHasNoPrefill_Then_TheInstructionReachesTheClient()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("ask", async (IReplInteractionChannel channel) =>
				await channel.AskConfirmationAsync(name: "city", prompt: "Proceed?").ConfigureAwait(false)
					? "yes"
					: "no")).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "ask",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			result.IsError.Should().BeTrue();
			text.Should().Contain(
				"answer.city",
				because: "the exception exists to name the argument the caller must send, and it is the "
					+ "only thing that turns a refusal into something an agent can act on");
		}
	}

	[TestMethod]
	[Description("Regression guard: an options-group property setter is application code the binder invokes, like a service factory, and an exception escaping it must not reach the client. It is classified as a binding failure with no marker, so without provenance it passes for a diagnostic the binder wrote itself — and a setter can expose the same paths and application state a factory can.")]
	public async Task When_AnOptionsGroupSetterThrows_Then_ItsMessageIsWithheld()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("configure", (ThrowingOptions options) => options.Label ?? "ok")).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "configure",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["label"] = "x" })
				.ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			result.IsError.Should().BeTrue();
			text.Should().NotContain(
				"setter-marker-detail",
				because: "a property setter is application code, and what escapes it is not addressed to this caller");
		}
	}

	[ReplOptionsGroup]
	public sealed class ThrowingOptions
	{
		private string? _label;

		public string? Label
		{
			get => _label;
			set
			{
				_label = value;
				throw new InvalidOperationException("setter-marker-detail");
			}
		}
	}

	[TestMethod]
	[Description("Regression guard: a dependency factory that runs its own budget and gives up has failed like any other, and its message must be withheld too. Excluding cancellation by the exception's type left it unmarked, so it passed for a diagnostic the binder wrote — cancellation is told apart by who asked for it, and the caller here never withdrew.")]
	public async Task When_ADependencyFactoryCancelsItself_Then_ItsMessageIsWithheld()
	{
		var session = await McpTestFixture.CreateAsync(
			configure: app => app.Map("probe", (IThrowingDependency dependency) => dependency.ToString() ?? "ok"),
			configureOptions: null,
			clientOptions: null,
			configureServices: services => services.AddSingleton<IThrowingDependency>(
				implementationFactory: static _ => throw new OperationCanceledException("cancel-marker-detail")))
			.ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "probe",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			text.Should().NotContain(
				"cancel-marker-detail",
				because: "the caller never withdrew, so this is the factory failing rather than a withdrawal");
		}
	}

	[TestMethod]
	[Description("Regression guard: an explicitly registered prompt handler that reports feedback must not lose it. On 2026-07-28 a request that declared no log level receives no message notifications, so the buffer is the only carrier \u2014 and this primitive opened none, because the SDK invokes its handler directly rather than through the adapter that opens one for every command-backed path.")]
	public async Task When_AnExplicitPromptReports_Then_TheFeedbackRidesInTheResult()
	{
		var session = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.Prompt(
				"brief",
				static async Task<string> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"prompt-notice",
						cancellationToken).ConfigureAwait(false);
					return "drafted";
				})).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var result = await session.Client.GetPromptAsync(
				"brief",
				arguments: null,
				cancellationToken: CancellationToken.None).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Messages.Select(static m => (m.Content as TextContentBlock)?.Text ?? string.Empty));

			text.Should().Contain(
				"prompt-notice",
				because: "a modern request that declared no log level has nowhere else to receive it");
		}
	}

	[TestMethod]
	[Description("Regression guard: appending feedback to an explicitly registered prompt's result must not cost the rest of it. The wrapper rebuilds the result to extend its messages, and a rebuild that names fields by hand drops the ones it forgot without a trace — here _meta, which the handler set and the client was meant to read.")]
	public async Task When_AnExplicitPromptReportsBesideMetadata_Then_TheWholeResultSurvives()
	{
		var session = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.Prompt(
				"brief",
				static async Task<GetPromptResult> (IMcpFeedback feedback, CancellationToken cancellationToken) =>
				{
					await feedback.SendMessageAsync(
						McpMessageLevel.Warning,
						"prompt-notice",
						cancellationToken).ConfigureAwait(false);

					return new GetPromptResult
					{
						Description = "kept-description",
						Meta = new JsonObject { ["sentinel"] = "kept" },
						Messages =
						[
							new PromptMessage
							{
								Role = Role.User,
								Content = new TextContentBlock { Text = "drafted" },
							},
						],
					};
				})).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			// The precondition the rebuild depends on: without a declared log level there is no
			// notification channel, so the notice has to ride in the result and the wrapper runs.
			session.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

			var result = await session.Client.GetPromptAsync(
				"brief",
				arguments: null,
				cancellationToken: CancellationToken.None).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Messages.Select(static m => (m.Content as TextContentBlock)?.Text ?? string.Empty));

			text.Should().Contain("drafted", because: "the payload the handler produced comes first");
			text.Should().Contain("prompt-notice", because: "the notice rides after it");

			result.Description.Should().Be(
				"kept-description",
				because: "the handler described its own prompt");
			// Read the node out before asserting: a null-conditional chain short-circuits Should() too,
			// and the SDK stamps its own serverInfo entry, so a non-null _meta proves nothing here.
			var sentinel = result.Meta?["sentinel"]?.GetValue<string>();
			sentinel.Should().Be(
				"kept",
				because: "only the wrapper stood between the metadata the handler set and the client");
		}
	}

	[TestMethod]
	[Description("Contract guard on the SDK surface rather than on one result: McpExplicitPrompt rebuilds a GetPromptResult field by field, so a field a later SDK adds would be dropped from every prompt result carrying feedback, and nothing would say so. When this goes red after an SDK bump, copy the new field in the wrapper before widening the list here.")]
	public void When_TheSdkPromptResultCarriesAField_Then_TheWrapperCopiesIt()
	{
		var carried = typeof(GetPromptResult)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(static property => property.CanWrite)
			.Select(static property => property.Name)
			.Order(StringComparer.Ordinal);

		carried.Should().Equal(
			["Description", "Messages", "Meta", "ResultType"],
			because: "McpExplicitPrompt.GetAsync copies exactly these when it appends feedback");
	}

	[TestMethod]
	[Description("Regression guard: an explicitly registered prompt handler must see the connection\u0027s native roots. Every other execution path primes them before the handler runs, so a handler reading Current here would see an empty list and take it for a client that declared no workspace \u2014 a difference it has no way to detect.")]
	public async Task When_AnExplicitPromptReadsRoots_Then_TheyAreResolved()
	{
		// Roots are deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but still supported by
		// Repl.Mcp until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		var clientOptions = new McpClientOptions
		{
			Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
			Handlers = new McpClientHandlers
			{
				RootsHandler = static (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = [new Root { Uri = "file:///C:/workspace", Name = "workspace" }],
				}),
			},
		};
#pragma warning restore MCP9005

		var session = await McpTestFixture.CreateAsync(
			_ => { },
			options => options.Prompt(
				"where",
				static (IMcpClientRoots roots) =>
					string.Join(',', roots.Current.Select(static r => r.Uri.ToString()))),
			clientOptions).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.GetPromptAsync(
				"where",
				arguments: null,
				cancellationToken: CancellationToken.None).ConfigureAwait(false);

			var text = string.Join(
				separator: '\n',
				values: result.Messages.Select(static m => (m.Content as TextContentBlock)?.Text ?? string.Empty));

			text.Should().Contain(
				"file:///C:/workspace",
				because: "Current promises the connection\u0027s effective roots on every execution path");
		}
	}

	/// <summary>A dependency no test registers successfully; only its resolution path matters.</summary>
	public interface IThrowingDependency;

	[TestMethod]
	[Description("Regression guard: an exception the handler never caught must not reach the client verbatim. The framework renders that message for an operator at a console, and it routinely carries a path, a parameter and its CLR type, or a connection string \u2014 over MCP the reader is a remote client instead. App-authored feedback still travels, because the app wrote it for that reader.")]
	public async Task When_AResourceHandlerThrows_Then_TheExceptionTextIsWithheld()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("leaky", async (IMcpFeedback feedback, CancellationToken ct) =>
				{
					await feedback.SendMessageAsync(McpMessageLevel.Warning, "render-notice", ct).ConfigureAwait(false);
					throw new InvalidOperationException("secret-internal-detail");
				})
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var act = async () => await session.Client.ReadResourceAsync("repl://leaky").ConfigureAwait(false);

			var message = (await act.Should().ThrowAsync<Exception>().ConfigureAwait(false)).Which.Message;

			message.Should().NotContain(
				"secret-internal-detail",
				because: "the framework rendered that text for an operator, not for a remote caller");
			message.Should().Contain(
				"render-notice",
				because: "the app authored that message for the client and a failed read has no body for it");
		}
	}

	[TestMethod]
	[Description("Regression guard: the same withholding on the tool path, which is the one clients actually call. A tool result carries its text as content rather than as an exception, so the SDK never sanitizes it and this is the only thing that does.")]
	public async Task When_AToolHandlerThrows_Then_TheExceptionTextIsWithheld()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("leaky", string () =>
				throw new InvalidOperationException("secret-internal-detail"))).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "leaky",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				'\n',
				result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			result.IsError.Should().BeTrue();
			text.Should().NotContain(
				"secret-internal-detail",
				because: "a thrown handler is not the app speaking to the client");
		}
	}

	[TestMethod]
	[Description("Pins the other side of the same rule: a failure the handler CHOSE to return is the app speaking, and must reach the client word for word. Withholding it would turn every actionable error into a bare exit code, which is the regression this guard exists to catch.")]
	public async Task When_AHandlerReturnsAFailure_Then_ItsOwnTextStillReaches()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("refuse", () => Results.Error("bad-env", "environment must be staging or production"))).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.CallToolAsync(
				toolName: "refuse",
				arguments: new Dictionary<string, object?>(StringComparer.Ordinal)).ConfigureAwait(false);

			var text = string.Join(
				'\n',
				result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

			text.Should().Contain(
				"environment must be staging or production",
				because: "the handler authored that for whoever called it, and withholding it helps nobody");
		}
	}

	[TestMethod]
	[Description("An explicitly registered prompt can declare an MCP capability service as a parameter. Issue #96 reported this failing the prompts/get request outright; McpExplicitPrompt's replacement of request.Services already resolves it, and this pins that it stays so.")]
	public async Task When_AnExplicitPromptInjectsSampling_Then_ItResolves()
	{
		var text = await GetPromptTextAsync(static (IMcpSampling sampling) => sampling is null ? "null" : "resolved")
			.ConfigureAwait(false);

		text.Should().Be("resolved");
	}

	[TestMethod]
	[Description("A prompt that declares IServiceProvider and resolves a capability service from it must find it. The overlay answered IServiceProvider with the provider it wraps, so a handler asking it for IServiceProvider got the inner container back, which knows nothing of IMcpSampling, IMcpClientRoots, IMcpElicitation or IMcpFeedback.")]
	public async Task When_AnExplicitPromptResolvesACapabilityThroughItsServiceProvider_Then_ItIsFound()
	{
		var text = await GetPromptTextAsync(ResolveSamplingThroughProvider).ConfigureAwait(false);

		text.Should().Be("sp-ok");
	}

	[TestMethod]
	[Description("The same as the mcp serve case above, on the reusable BuildMcpServerOptions() path, which issue #96 names as reproducing too: there the SDK dispatches straight into the pre-built prompt, with no Repl request handler in between.")]
	public async Task When_AReusableOptionsPromptResolvesACapabilityThroughItsServiceProvider_Then_ItIsFound()
	{
		var app = ReplApp.Create();
		var mcpOptions = app.BuildMcpServerOptions(options => options.Prompt("probe", ResolveSamplingThroughProvider));

		var text = await GetReusablePromptTextAsync(mcpOptions).ConfigureAwait(false);

		text.Should().Be("sp-ok");
	}

	[TestMethod]
	[Description("The reusable path without an app provider: ICoreReplApp.BuildMcpServerOptions() wraps an empty provider that knows nothing, so the overlay alone has to supply both the capability service and IServiceProvider itself. Pins that a prompt declaring IServiceProvider still binds it as a dependency there. It passed before the overlay's IsService was aligned with its GetService — the SDK binds an IServiceProvider parameter itself, without asking IsService — so this is a guard for the path, not a reproduction.")]
	public async Task When_APromptDeclaresIServiceProviderWithoutAnAppProvider_Then_ItIsBoundAsADependency()
	{
		var app = ReplApp.Create();
		var mcpOptions = app.Core.BuildMcpServerOptions(options => options.Prompt("probe", ResolveSamplingThroughProvider));

		var text = await GetReusablePromptTextAsync(mcpOptions).ConfigureAwait(false);

		text.Should().Be("sp-ok");
	}

	private static readonly Func<IServiceProvider, string> ResolveSamplingThroughProvider =
		static services => services.GetService(typeof(IMcpSampling)) is null ? "sp-null" : "sp-ok";

	private static async Task<string> GetReusablePromptTextAsync(McpServerOptions mcpOptions)
	{
		var session = await McpPipeSession.StartAsync(
			async (io, token) =>
			{
				var transport = new StreamServerTransport(io.InputStream, io.OutputStream, "reusable-options-server");
				var server = McpServer.Create(transport, mcpOptions);
				try
				{
					await server.RunAsync(token).ConfigureAwait(false);
				}
				finally
				{
					await server.DisposeAsync().ConfigureAwait(false);
					await transport.DisposeAsync().ConfigureAwait(false);
				}
			},
			clientOptions: null,
			CancellationToken.None).ConfigureAwait(false);
		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.GetPromptAsync("probe", arguments: null).ConfigureAwait(false);
			return string.Join('\n', result.Messages.Select(static m => (m.Content as TextContentBlock)?.Text ?? string.Empty));
		}
	}

	private static async Task<string> GetPromptTextAsync(Delegate handler)
	{
		var session = await McpTestFixture.CreateAsync(_ => { }, options => options.Prompt("probe", handler)).ConfigureAwait(false);
		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.GetPromptAsync("probe", arguments: null).ConfigureAwait(false);
			return string.Join('\n', result.Messages.Select(static m => (m.Content as TextContentBlock)?.Text ?? string.Empty));
		}
	}
}
