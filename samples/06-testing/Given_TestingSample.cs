namespace Samples.Testing;

[TestClass]
[DoNotParallelize]
public sealed class Given_TestingSample
{
	[TestMethod]
	[Description("Shows command execution with text output assertions.")]
	public async Task When_RunningSimpleCommand_Then_OutputIsAvailable()
	{
		await using var host = ReplTestHost.Create(() => SampleReplApp.Create());
		await using var session = await host.OpenSessionAsync();

		var execution = await session.RunCommandAsync("ping --no-logo");

		execution.ExitCode.Should().Be(0);
		execution.OutputText.Should().Contain("pong");
	}

	[TestMethod]
	[Description("Shows typed result assertions from one command.")]
	public async Task When_CommandReturnsObject_Then_TypedAssertionsArePossible()
	{
		await using var host = ReplTestHost.Create(() => SampleReplApp.Create());
		await using var session = await host.OpenSessionAsync();

		var execution = await session.RunCommandAsync("widget show 1 --json --no-logo");

		execution.GetResult<SampleReplApp.Widget>().Name.Should().Be("Alpha");
		execution.OutputText.Should().Contain("Alpha");
	}

	[TestMethod]
	[Description("Shows semantic interaction assertions with captured events.")]
	public async Task When_CommandEmitsStatus_Then_InteractionEventsCanBeAsserted()
	{
		await using var host = ReplTestHost.Create(() => SampleReplApp.Create());
		await using var session = await host.OpenSessionAsync();

		var execution = await session.RunCommandAsync("import --no-logo");

		execution.InteractionEvents
			.OfType<ReplStatusEvent>()
			.Should()
			.ContainSingle(evt => string.Equals(evt.Text, "Import started", StringComparison.Ordinal));
		execution.TimelineEvents.OfType<ResultProducedEvent>().Should().ContainSingle();
	}

	[TestMethod]
	[Description("Shows multi-session simulation and snapshot assertions.")]
	public async Task When_MultipleSessionsAreOpen_Then_SessionSnapshotsExposeMetadata()
	{
		await using var host = ReplTestHost.Create(() => SampleReplApp.Create());
		await using var ws = await host.OpenSessionAsync(new SessionDescriptor
		{
			TransportName = "websocket",
			RemotePeer = "::1:60288",
			TerminalIdentity = "xterm-256color",
			WindowSize = (132, 43),
		});
		await using var telnet = await host.OpenSessionAsync(new SessionDescriptor
		{
			TransportName = "telnet",
			RemotePeer = "::1:45123",
			TerminalIdentity = "XTERM-256COLOR",
			WindowSize = (120, 40),
		});

		_ = await ws.RunCommandAsync("ping --no-logo");
		_ = await telnet.RunCommandAsync("ping --no-logo");
		var snapshots = await host.QuerySessionsAsync();

		snapshots.Should().HaveCount(2);
		var wsSnapshot = snapshots.Single(snapshot =>
			string.Equals(snapshot.SessionId, ws.SessionId, StringComparison.Ordinal));
		wsSnapshot.Transport.Should().Be("websocket");
		wsSnapshot.Screen.Should().Be((132, 43));
	}

	[TestMethod]
	[Description("Shows per-command timeout behavior for long-running handlers.")]
	public async Task When_CommandIsTooSlow_Then_TimeoutIsRaised()
	{
		await using var host = ReplTestHost.Create(
			() =>
			{
				var app = ReplApp.Create().UseDefaultInteractive();
				app.Map("slow", async (CancellationToken ct) =>
				{
					await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
					return "done";
				});
				return app;
			},
			options => options.CommandTimeout = TimeSpan.FromMilliseconds(50));
		await using var session = await host.OpenSessionAsync();

		Func<Task> action = () => session.RunCommandAsync("slow --no-logo").AsTask();

		var assertion = await action.Should().ThrowAsync<TimeoutException>();
		assertion.Which.Message.Should().Contain("timeout");
	}

	[TestMethod]
	[Description("Shows a realistic multi-step scenario with two sessions, shared state, typed assertions, and metadata checks.")]
	public async Task When_RunningComplexScenario_Then_EndToEndBehaviorRemainsReadable()
	{
		var shared = new SampleReplApp.SharedState();
		await using var host = ReplTestHost.Create(() => SampleReplApp.Create(shared));
		await using var admin = await host.OpenSessionAsync(new SessionDescriptor
		{
			TransportName = "signalr",
			RemotePeer = "::1:41957",
			TerminalIdentity = "xterm-256color",
			WindowSize = (140, 45),
		});
		await using var operatorSession = await host.OpenSessionAsync(new SessionDescriptor
		{
			TransportName = "websocket",
			RemotePeer = "::1:60288",
			TerminalIdentity = "xterm-256color",
			WindowSize = (120, 40),
		});

		var setMaintenance = await admin.RunCommandAsync("settings set maintenance on --no-logo");
		var showMaintenance = await operatorSession.RunCommandAsync("settings show maintenance --no-logo");
		var increment1 = await admin.RunCommandAsync("counter inc --no-logo");
		var increment2 = await operatorSession.RunCommandAsync("counter inc --no-logo");
		var counterReadback = await admin.RunCommandAsync("counter get --no-logo");
		var widget = await operatorSession.RunCommandAsync("widget show 2 --json --no-logo");
		var import = await admin.RunCommandAsync("import --no-logo");

		setMaintenance.ExitCode.Should().Be(0);
		showMaintenance.OutputText.Should().Contain("on");
		increment1.GetResult<int>().Should().Be(1);
		increment2.GetResult<int>().Should().Be(2);
		counterReadback.GetResult<int>().Should().Be(2);
		widget.GetResult<SampleReplApp.Widget>().Name.Should().Be("Beta");
		widget.OutputText.Should().Contain("Beta");
		import.InteractionEvents
			.OfType<ReplStatusEvent>()
			.Should()
			.ContainSingle(evt => string.Equals(evt.Text, "Import started", StringComparison.Ordinal));

		var snapshots = await host.QuerySessionsAsync();
		snapshots.Should().HaveCount(2);
		var adminSnapshot = snapshots.Single(snapshot =>
			string.Equals(snapshot.SessionId, admin.SessionId, StringComparison.Ordinal));
		adminSnapshot.Transport.Should().Be("signalr");
		adminSnapshot.Screen.Should().Be((140, 45));
	}
}
