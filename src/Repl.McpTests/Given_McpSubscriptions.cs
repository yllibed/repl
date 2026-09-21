using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Covers <c>subscriptions/listen</c> (SEP-2575) delivery over the in-process stream transport that
/// stands in for stdio, and the SDK behaviour Repl's discovery signal depends on.
/// </summary>
[TestClass]
public sealed class Given_McpSubscriptions
{
	[TestMethod]
	[Description("Pins the undocumented SDK behaviour the discovery signal rests on: clearing an already-empty primitive collection must still raise Changed. McpServerHandler uses empty collections as pure list-changed signals, so if a future SDK turns Clear() into a no-op when the collection is empty, discovery notifications would silently stop; this test fails loudly instead.")]
	public void When_ClearingAnEmptyCollection_Then_ChangedStillFires()
	{
		var resources = new McpServerResourceCollection();
		var tools = new McpServerPrimitiveCollection<McpServerTool>();
		var resourceSignals = 0;
		var toolSignals = 0;
		resources.Changed += (_, _) => resourceSignals++;
		tools.Changed += (_, _) => toolSignals++;

		resources.Clear();
		tools.Clear();

		resourceSignals.Should().Be(1);
		toolSignals.Should().Be(1);
		resources.Count.Should().Be(0, because: "the signal must not mutate anything a client could observe");
		tools.Count.Should().Be(0, because: "the signal must not mutate anything a client could observe");
	}

	[TestMethod]
	[Description("Guards backward compatibility while the modern path is filtered: an initialize-era client that pins 2025-11-25 and opens NO subscription must still receive tools/list_changed as an unsolicited session-wide broadcast. Delegating fan-out to the SDK must not cost existing hosts their discovery notifications — the server stays multi-revision and the SDK picks the delivery mode per client.")]
	public async Task When_LegacyClientNeverSubscribes_Then_ListChangedIsStillBroadcast()
	{
		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("alpha", () => "a"),
			configureOptions: null,
			clientOptions: new McpClientOptions { ProtocolVersion = McpProtocolRevisions.LastWithSessions })
			.ConfigureAwait(false);
		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.LastWithSessions);

		var toolsChanged = new TaskCompletionSource<JsonRpcNotification>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = Capture(fixture, NotificationMethods.ToolListChangedNotification, toolsChanged);
		await using var registrationScope = registration.ConfigureAwait(false);

		fixture.App.Map("late", () => "l");
		fixture.App.Core.InvalidateRouting();

		await toolsChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Confirms subscriptions/listen reaches a stream-transport (stdio-shaped) server and that the SDK's built-in handler acknowledges the filters it grants. Repl delegates list-changed fan-out to that pipeline, so this is the precondition the delegation rests on.")]
	public async Task When_ClientOpensSubscriptionsListen_Then_ServerAcknowledges()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map("alpha", () => "a"))
			.ConfigureAwait(false);
		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var acknowledged = new TaskCompletionSource<JsonRpcNotification>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = Capture(fixture, NotificationMethods.SubscriptionsAcknowledgedNotification, acknowledged);
		await using var registrationScope = registration.ConfigureAwait(false);

		using var listenCts = new CancellationTokenSource();
		var listenTask = OpenListenAsync(
			fixture,
			new SubscriptionsListenNotifications { ToolsListChanged = true },
			listenCts.Token);

		var notification = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		var granted = notification.Params
			.Deserialize<SubscriptionsAcknowledgedNotificationParams>(McpJsonUtilities.DefaultOptions);
		granted.Should().NotBeNull();
		granted!.Notifications.ToolsListChanged.Should().BeTrue();

		await CloseListenAsync(listenTask, listenCts).ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Guards SEP-2575 delivery filtering: a 2026-07-28 client that subscribes to prompts/list_changed only must NOT receive tools/list_changed, and the notification it does receive must carry its listen request id. A server that sends */list_changed itself has no access to the subscription registry, so it delivers every type to every client, untagged.")]
	public async Task When_ClientSubscribesToPromptsOnly_Then_ToolListChangedIsNotDelivered()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map("alpha", () => "a"))
			.ConfigureAwait(false);
		fixture.Client.NegotiatedProtocolVersion.Should().Be(McpProtocolRevisions.Sessionless);

		var toolsChanged = new TaskCompletionSource<JsonRpcNotification>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var promptsChanged = new TaskCompletionSource<JsonRpcNotification>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var toolsRegistration = Capture(fixture, NotificationMethods.ToolListChangedNotification, toolsChanged);
		await using var toolsScope = toolsRegistration.ConfigureAwait(false);
		var promptsRegistration = Capture(fixture, NotificationMethods.PromptListChangedNotification, promptsChanged);
		await using var promptsScope = promptsRegistration.ConfigureAwait(false);

		using var listenCts = new CancellationTokenSource();
		var listenTask = OpenListenAsync(
			fixture,
			new SubscriptionsListenNotifications { PromptsListChanged = true },
			listenCts.Token);

		fixture.App.Map("late", () => "l");
		fixture.App.Core.InvalidateRouting();

		// Discovery signals fire tools-then-resources-then-prompts, so observing the prompts
		// notification proves the tools one has already had its chance.
		var prompts = await promptsChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		// Render to a string rather than asserting on the JsonNode: NotBeNull on a node reached via
		// ?. is vacuous (the null-conditional result satisfies it even when the payload is absent),
		// and the listen request id is a JSON-RPC id, so it may be a number as well as a string.
		var subscriptionId = prompts.Params?["_meta"]?[MetaKeys.SubscriptionId]?.ToJsonString();
		subscriptionId.Should().NotBeNullOrEmpty(
			because: "SEP-2575 requires every subscription notification to carry its listen request id");
		toolsChanged.Task.IsCompleted.Should().BeFalse(
			because: "the client never subscribed to tools/list_changed");

		await CloseListenAsync(listenTask, listenCts).ConfigureAwait(false);
	}

	private static IAsyncDisposable Capture(
		McpTestFixture fixture,
		string method,
		TaskCompletionSource<JsonRpcNotification> received) =>
		fixture.Client.RegisterNotificationHandler(
			method,
			(notification, _) =>
			{
				received.TrySetResult(notification);
				return ValueTask.CompletedTask;
			});

	/// <remarks>
	/// <c>subscriptions/listen</c> is a long-lived request: the response is held open for the
	/// subscription's lifetime, so it must not be awaited until the stream is cancelled.
	/// </remarks>
	private static Task<EmptyResult> OpenListenAsync(
		McpTestFixture fixture,
		SubscriptionsListenNotifications filters,
		CancellationToken cancellationToken) =>
		fixture.Client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
			RequestMethods.SubscriptionsListen,
			new SubscriptionsListenRequestParams { Notifications = filters },
			cancellationToken: cancellationToken)
			.AsTask();

	private static async Task CloseListenAsync(Task<EmptyResult> listenTask, CancellationTokenSource listenCts)
	{
		await listenCts.CancelAsync().ConfigureAwait(false);
		try
		{
			// The listen request is deliberately started by the caller and awaited only once its
			// stream has been cancelled. MSTest runs without a synchronization context, so the
			// deadlock VSTHRD003 guards against cannot arise here.
#pragma warning disable VSTHRD003
			await listenTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (OperationCanceledException)
		{
			// Expected: the listen stream ends on cancellation.
		}
	}
}
