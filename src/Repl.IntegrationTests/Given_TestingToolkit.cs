using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TestingToolkit
{
	[TestMethod]
	[Description("Regression guard: verifies one command execution with typed and textual assertions without console redirection.")]
	public async Task When_RunningCommandInSession_Then_ResultContainsOutputAndExitCode()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("hello", () => "world");
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		var result = await session.RunCommandAsync("hello --no-logo");

		result.ExitCode.Should().Be(0);
		result.OutputText.Should().Contain("world");
	}

	[TestMethod]
	[Description("Regression guard: verifies multi-step session flows stay readable while keeping command state in the same live session.")]
	public async Task When_RunningMultipleCommandsInSameSession_Then_StatePersists()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			var value = 0;
			app.Map("set", () =>
			{
				value = 42;
				return "ok";
			});
			app.Map("get", () => value);
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		_ = await session.RunCommandAsync("set --no-logo");
		var result = await session.RunCommandAsync("get --no-logo");

		result.ExitCode.Should().Be(0);
		result.OutputText.Should().Contain("42");
	}

	[TestMethod]
	[Description("Regression guard: verifies concurrent sessions can run independently while observing shared state updates.")]
	public async Task When_RunningConcurrentSessions_Then_SharedStateIsVisible()
	{
		var shared = 0;
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("inc", () => Interlocked.Increment(ref shared));
			app.Map("get", () => shared);
			return app;
		});
		await using var first = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "websocket" });
		await using var second = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "signalr" });

		var firstIncrement = first.RunCommandAsync("inc --no-logo").AsTask();
		var secondIncrement = second.RunCommandAsync("inc --no-logo").AsTask();
		await Task.WhenAll(firstIncrement, secondIncrement);
		var readback = await first.RunCommandAsync("get --no-logo");

		readback.GetResult<int>().Should().Be(2);
	}

	[TestMethod]
	[Description("Regression guard: verifies typed result access so tests can assert semantic payloads without text parsing.")]
	public async Task When_CommandReturnsObject_Then_TypedResultIsAvailable()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("contact show", () => new Contact(42, "Alice"));
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		var result = await session.RunCommandAsync("contact show --json --no-logo");

		result.GetResult<Contact>().Name.Should().Be("Alice");
		result.ReadJson<Contact>().Id.Should().Be(42);
	}

	[TestMethod]
	[Description("Regression guard: verifies interaction and timeline events are captured per command for semantic assertions.")]
	public async Task When_CommandEmitsInteraction_Then_EventsAreCaptured()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("import", async (IReplInteractionChannel channel, CancellationToken ct) =>
			{
				await channel.WriteStatusAsync("Import started", ct).ConfigureAwait(false);
				return "done";
			});
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		var result = await session.RunCommandAsync("import --no-logo");

		result.InteractionEvents
			.OfType<ReplStatusEvent>()
			.Should()
			.ContainSingle(evt => string.Equals(evt.Text, "Import started", StringComparison.Ordinal));
		result.TimelineEvents.OfType<ResultProducedEvent>().Should().ContainSingle();
	}

	[TestMethod]
	[Description("Regression guard: verifies semantic notice, warning, and problem events are captured by the testing toolkit.")]
	public async Task When_CommandEmitsSemanticFeedback_Then_FeedbackEventsAreCaptured()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
			{
				await channel.WriteNoticeAsync("Connected", ct).ConfigureAwait(false);
				await channel.WriteWarningAsync("Token expires soon", ct).ConfigureAwait(false);
				await channel.WriteProblemAsync("Sync failed", "Retry later.", "sync_failed", ct).ConfigureAwait(false);
				return "done";
			});
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		var result = await session.RunCommandAsync("sync --no-logo");

		result.InteractionEvents.OfType<ReplNoticeEvent>()
			.Should().ContainSingle(evt => string.Equals(evt.Text, "Connected", StringComparison.Ordinal));
		result.InteractionEvents.OfType<ReplWarningEvent>()
			.Should().ContainSingle(evt => string.Equals(evt.Text, "Token expires soon", StringComparison.Ordinal));
		result.InteractionEvents.OfType<ReplProblemEvent>()
			.Should().ContainSingle(evt =>
				string.Equals(evt.Summary, "Sync failed", StringComparison.Ordinal)
				&& string.Equals(evt.Code, "sync_failed", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Regression guard: verifies metadata snapshots expose transport, remote and terminal information for active sessions.")]
	public async Task When_QueryingSessions_Then_MetadataSnapshotContainsDescriptorData()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("ping", () => "pong");
			return app;
		});

		var descriptor = new SessionDescriptor
		{
			TransportName = "telnet",
			RemotePeer = "::1:45123",
			TerminalIdentity = "xterm-256color",
			WindowSize = (120, 40),
			TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ResizeReporting,
		};
		await using var session = await host.OpenSessionAsync(descriptor);
		_ = await session.RunCommandAsync("ping --no-logo");

		var snapshots = await host.QuerySessionsAsync();
		var current = snapshots.Single(snapshot =>
			string.Equals(snapshot.SessionId, session.SessionId, StringComparison.Ordinal));

		current.Transport.Should().Be("telnet");
		current.Remote.Should().Be("::1:45123");
		current.Terminal.Should().Be("xterm-256color");
		current.Screen.Should().Be((120, 40));
		current.Capabilities.Should().HaveFlag(TerminalCapabilities.ResizeReporting);
	}

	[TestMethod]
	[Description("Regression guard: verifies per-command timeout so hanging handlers fail quickly with explicit diagnostics.")]
	public async Task When_CommandExceedsTimeout_Then_RunCommandAsyncThrowsTimeoutException()
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
	[Description("Regression guard: verifies the per-command timeout still surfaces as TimeoutException when the app maps ExitCodes.Cancelled so that a hung command is never reported as an ordinary exit code.")]
	public async Task When_CommandExceedsTimeoutAndCancelledIsMapped_Then_RunCommandAsyncStillThrowsTimeoutException()
	{
		await using var host = ReplTestHost.Create(
			() =>
			{
				var app = ReplApp.Create().UseDefaultInteractive();
				app.Options(options => options.ExitCodes.Cancelled = 130);
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

		await action.Should().ThrowAsync<TimeoutException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies ANSI normalization defaults and can be disabled at host level.")]
	public async Task When_OutputContainsAnsi_Then_NormalizationBehaviorIsConfigurable()
	{
		await using var normalizedHost = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("status", () => "\u001b[31mALERT\u001b[0m");
			return app;
		});
		await using var normalizedSession = await normalizedHost.OpenSessionAsync();
		var normalizedResult = await normalizedSession.RunCommandAsync("status --no-logo");

		normalizedResult.OutputText.Should().Contain("ALERT");
		normalizedResult.OutputText.Should().NotContain("\u001b[");

		await using var rawHost = ReplTestHost.Create(
			() =>
			{
				var app = ReplApp.Create().UseDefaultInteractive();
				app.Map("status", () => "\u001b[31mALERT\u001b[0m");
				return app;
			},
			options => options.NormalizeAnsi = false);
		await using var rawSession = await rawHost.OpenSessionAsync();
		var rawResult = await rawSession.RunCommandAsync("status --no-logo");

		rawResult.OutputText.Should().Contain("\u001b[31mALERT\u001b[0m");
	}

	private sealed record Contact(int Id, string Name);

	[TestMethod]
	[Description("Regression guard: verifies a command timeout larger than a cancellation source can carry is refused where it is set. It has never worked — CancelAfter rejects it, so the failure surfaced from inside RunCommandAsync naming a 'delay' parameter the caller never passed. Only the ceiling is checked: zero and negatives have meant 'no timeout' since this shipped.")]
	public void When_TheCommandTimeoutCannotBoundAWait_Then_ItIsRefused()
	{
		var options = new ReplScenarioOptions();

		var act = () => options.CommandTimeout = TimeSpan.MaxValue;

		act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*at most*");
	}

	[TestMethod]
	[DataRow(0, DisplayName = "Zero")]
	[DataRow(-5, DisplayName = "Negative")]
	[Description("Regression guard: verifies the shipped 'a non-positive command timeout means no deadline' reading survives the ceiling check added beside it. Tightening the upper end must not quietly close the documented way to run unbounded.")]
	public async Task When_TheCommandTimeoutIsNotPositive_Then_TheCommandStillRuns(int seconds)
	{
		await using var host = ReplTestHost.Create(
			CreateEchoApp,
			options => options.CommandTimeout = TimeSpan.FromSeconds(seconds));
		await using var session = await host.OpenSessionAsync();

		var execution = await session.RunCommandAsync("echo");

		execution.ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Regression guard: verifies a null run-options factory is refused where it is set rather than dereferenced later. It surfaced as a bare NullReferenceException from inside session startup, which names nothing the caller touched.")]
	public void When_TheRunOptionsFactoryIsNull_Then_ItIsRefused()
	{
		var options = new ReplScenarioOptions();

		var act = () => options.RunOptionsFactory = null!;

		act.Should().Throw<ArgumentNullException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a run-options factory returning null fails with what the caller set, not a NullReferenceException from session startup. The setter cannot catch this one, so the call site has to.")]
	public async Task When_TheRunOptionsFactoryReturnsNull_Then_TheFailureNamesIt()
	{
		await using var host = ReplTestHost.Create(
			CreateEchoApp,
			options => options.RunOptionsFactory = static () => null!);

		var act = async () => await host.OpenSessionAsync().ConfigureAwait(false);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*RunOptionsFactory*");
	}

	private static ReplApp CreateEchoApp()
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
		});
		app.Map("echo", () => "echoed");
		return app;
	}

}
