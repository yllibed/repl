using AwesomeAssertions;
using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ProcessSignalHarness
{
	[TestMethod]
	[Description("Regression guard: verifies the first signal cancels a run in flight cooperatively, reports Interrupted, resolves the conventional 130, and lets the command's cleanup finish. Delivered from outside the run, which is what the harness exists for: before it, a signal could only be raised from inside the handler under test.")]
	public async Task When_TheFirstSignalArrives_Then_TheRunIsCancelledCooperatively()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var harness = ReplProcessSignalHarness.Create(() => CreateBlockingApp(started, cleanedUp));
		var run = await harness.StartRunAsync("work");
		// Starting a run guarantees a signal will reach it, not that the command body is executing yet:
		// the scope is installed before the arguments are even parsed. Asserting on cleanup means
		// waiting for the command to say it is running.
		await started.Task;

		var delivery = harness.SendSignal(ReplProcessSignal.Interrupt);
		var result = await run.Completion;

		delivery.Should().Be(ReplSignalDelivery.CancellationRequested);
		result.OutcomeKind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
		result.ExitCode.Should().Be(130);
		cleanedUp.Task.IsCompletedSuccessfully.Should().BeTrue(
			because: "a cooperative first signal must let the command unwind, not cut it off");
		harness.DiagnosticText.Should().Contain("cancelling active standalone runs");
	}

	[TestMethod]
	[Description("Regression guard: verifies SIGTERM claims a run in flight and carries the conventional 143, so the two signal kinds are not assumed to share one exit code.")]
	public async Task When_TerminateArrives_Then_TheRunResolvesTheSigTermCode()
	{
		await using var harness = ReplProcessSignalHarness.Create(() => CreateBlockingApp());
		var run = await harness.StartRunAsync("work");

		var delivery = harness.SendSignal(ReplProcessSignal.Terminate);
		var result = await run.Completion;

		delivery.Should().Be(ReplSignalDelivery.CancellationRequested);
		result.ExitCode.Should().Be(143);
		result.OutcomeKind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies a configured ExitCodes.Interrupted governs a signalled run, so the conventional code is a fallback rather than something the signal path hard-codes.")]
	public async Task When_InterruptedIsConfigured_Then_ItGovernsTheExitCode()
	{
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(configure: options => options.ExitCodes.Interrupted = 75));
		var run = await harness.StartRunAsync("work");

		harness.SendSignal(ReplProcessSignal.Interrupt);

		(await run.Completion).ExitCode.Should().Be(75);
	}

	[TestMethod]
	[Description("Regression guard: verifies a second signal reports that the operating system would take over and leaves the first claim's exit code intact, so escalating cannot relabel what the run is exiting with.")]
	public async Task When_ASecondSignalArrives_Then_ItWouldTerminateAndTheFirstClaimStands()
	{
		await using var harness = ReplProcessSignalHarness.Create(() => CreateBlockingApp());
		var run = await harness.StartRunAsync("work");

		var first = harness.SendSignal(ReplProcessSignal.Interrupt);
		var second = harness.SendSignal(ReplProcessSignal.Terminate);
		var third = harness.SendSignal(ReplProcessSignal.Terminate);
		var result = await run.Completion;

		first.Should().Be(ReplSignalDelivery.CancellationRequested);
		second.Should().Be(ReplSignalDelivery.WouldTerminateProcess);
		third.Should().Be(
			ReplSignalDelivery.WouldTerminateProcess,
			because: "the decision stays the same while the epoch is open, rather than resetting");
		result.ExitCode.Should().Be(130, because: "the first claim owns the exit code");
		harness.DiagnosticText.Should().Contain("allowing immediate operating-system termination");
	}

	[TestMethod]
	[Description("Regression guard: verifies a run that starts while a signal is already claimed inherits that epoch and is cancelled as it joins, instead of reading the next signal as a fresh first one. Needs two runs genuinely in flight, which is why starting a run does not serialise against the runs already going.")]
	public async Task When_ARunJoinsAfterAClaim_Then_ItInheritsTheEpoch()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		// Held so the first run cannot finish and reset the epoch: once the last run of a claimed epoch
		// drains, the claim is cleared and the next signal is a first signal again. The window a late
		// joiner lives in is exactly "claimed, and still occupied".
		using var holdFirstRunInCleanup = new SemaphoreSlim(initialCount: 0, maxCount: 1);
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(started, cleanupGate: holdFirstRunInCleanup));
		var first = await harness.StartRunAsync("work");
		await started.Task;
		harness.SendSignal(ReplProcessSignal.Interrupt);

		var lateJoiner = await harness.StartRunAsync("work");
		var lateResult = await lateJoiner.Completion;
		holdFirstRunInCleanup.Release();

		lateResult.OutcomeKind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
		lateResult.ExitCode.Should().Be(130, because: "the late joiner inherits the claimed signal's code");
		(await first.Completion).ExitCode.Should().Be(130);
	}

	[TestMethod]
	[Description("Regression guard: verifies a signal delivered with no run in flight is left to the operating-system default, so the harness cannot claim an epoch the real registration would have ignored.")]
	public async Task When_NoRunIsInFlight_Then_TheSignalIsNotHandled()
	{
		await using var harness = ReplProcessSignalHarness.Create(() => CreateBlockingApp());

		harness.SendSignal(ReplProcessSignal.Interrupt).Should().Be(ReplSignalDelivery.NotHandled);
	}

	[TestMethod]
	[Description("Regression guard: verifies Ctrl+Break counts as a signal on a declared Windows platform and is ignored on a declared Unix one. Both run on every host, which is the point: before the platform could be declared, half of this pair was unverifiable on any given machine.")]
	[DataRow(true, ReplSignalDelivery.CancellationRequested, DisplayName = "Declared Windows: Ctrl+Break is a signal")]
	[DataRow(false, ReplSignalDelivery.NotHandled, DisplayName = "Declared Unix: Ctrl+Break is not a signal")]
	public async Task When_BreakIsDelivered_Then_OnlyADeclaredWindowsPlatformHandlesIt(
		bool declareWindows,
		ReplSignalDelivery expected)
	{
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(),
			options => options.Platform = declareWindows
				? ReplPlatformProfile.Windows
				: ReplPlatformProfile.Unix);
		var run = await harness.StartRunAsync("work");

		harness.SendSignal(ReplProcessSignal.Break).Should().Be(expected);

		// Release the run either way, so the assertion above is what fails a broken case rather than
		// the harness blocking until its timeout.
		harness.SendSignal(ReplProcessSignal.Interrupt);
		await run.Completion;
	}

	[TestMethod]
	[Description("Regression guard: verifies a declared platform without a signal bridge degrades to caller-owned handling and says so once, rather than silently leaving a run unprotected. Asserted from any host.")]
	public async Task When_TheDeclaredPlatformHasNoBridge_Then_TheRunSaysHandlingIsCallerOwned()
	{
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateEchoApp(),
			options => options.Platform = ReplPlatformProfile.Browser);

		var run = await harness.StartRunAsync("echo");
		var result = await run.Completion;

		result.DiagnosticText.Should().Contain("unavailable on this platform");
		result.ExitCode.Should().Be(0, because: "a missing bridge degrades handling, it does not fail the run");
	}

	[TestMethod]
	[Description("Regression guard: verifies a registration the environment refuses degrades to caller-owned handling with a diagnostic naming the failure. No supported platform refuses one on demand, so an injected fault is the only way to cover the path an operator would actually hit.")]
	public async Task When_RegistrationIsRefused_Then_TheDegradationIsDiagnosed()
	{
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateEchoApp(),
			options => options.RegistrationFault = new PlatformNotSupportedException("registration refused"));

		var run = await harness.StartRunAsync("echo");
		var result = await run.Completion;

		result.DiagnosticText.Should().Contain("Failed to install automatic process-signal handling");
		result.DiagnosticText.Should().Contain("registration refused");
	}

	[TestMethod]
	[Description("Regression guard: verifies a second live harness is refused. Taking ownership tears down and reinstalls process-global registration state, so two harnesses would corrupt each other's isolation rather than merely race on the application under test — the failure has to be loud, not intermittent.")]
	public async Task When_ASecondHarnessIsCreated_Then_ItIsRefused()
	{
		await using var harness = ReplProcessSignalHarness.Create(() => CreateEchoApp());

		var second = () => ReplProcessSignalHarness.Create(() => CreateEchoApp());

		second.Should().Throw<InvalidOperationException>()
			.WithMessage("*already active in this process*");
	}

	[TestMethod]
	[Description("Regression guard: verifies a harness released by disposal can be replaced, so the exclusivity guard cannot leave a suite unable to run a second signal test.")]
	public async Task When_AHarnessIsDisposed_Then_AnotherCanBeCreated()
	{
		await using (var first = ReplProcessSignalHarness.Create(() => CreateEchoApp()))
		{
			var firstRun = await first.StartRunAsync("echo");
			await firstRun.Completion;
		}

		await using var second = ReplProcessSignalHarness.Create(() => CreateEchoApp());
		var secondRun = await second.StartRunAsync("echo");

		(await secondRun.Completion).ExitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Regression guard: verifies a blank command line is refused before a run starts, so a typo cannot silently start a run with no command and leave a signal test asserting against nothing.")]
	public async Task When_TheCommandLineIsBlank_Then_StartingItIsRefused()
	{
		await using var harness = ReplProcessSignalHarness.Create(() => CreateEchoApp());

		var act = async () => await harness.StartRunAsync(" ").ConfigureAwait(false);

		await act.Should().ThrowAsync<ArgumentException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a cancellation callback that throws while the run unwinds is reported on the run's diagnostics rather than swallowed. Until now this was only provable through internal APIs a package consumer cannot reach, which is the gap this toolkit exists to close.")]
	public async Task When_ACancellationCallbackThrows_Then_TheRunReportsIt()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var harness = ReplProcessSignalHarness.Create(() =>
		{
			var app = CreateApp(configure: null);
			app.Map("work", async (CancellationToken cancellationToken) =>
			{
				// Registered on the signal-linked token, so the failure happens on the cancellation path
				// the signal drives, not on an unrelated one.
				using var registration = cancellationToken.Register(
					static () => throw new InvalidOperationException("callback refused to unwind"));
				started.TrySetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				return "unreachable";
			});

			return app;
		});

		var run = await harness.StartRunAsync("work");
		await started.Task;
		harness.SendSignal(ReplProcessSignal.Interrupt);
		var result = await run.Completion;

		result.DiagnosticText.Should().Contain("callback refused to unwind");
	}

	[TestMethod]
	[Description("Regression guard: verifies a run that produced its own outcome keeps it when a signal lands, rather than having it replaced by the interruption code. This is the precedence rule the framework calls IsInterruptible, and it decides whether a command's reported failure survives a Ctrl+C that arrives while it renders.")]
	public async Task When_TheRunProducedItsOwnFailure_Then_TheSignalDoesNotRelabelIt()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var harness = ReplProcessSignalHarness.Create(() =>
		{
			var app = CreateApp(configure: null);
			app.Map("work", async (CancellationToken cancellationToken) =>
			{
				started.TrySetResult();
				// Waits for the signal, then ends with its own explicit code instead of propagating.
				try
				{
					await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Deliberately swallowed: the point is a run that resolves its own outcome.
				}

				return Results.Exit(7);
			});

			return app;
		});

		var run = await harness.StartRunAsync("work");
		await started.Task;
		harness.SendSignal(ReplProcessSignal.Interrupt);
		var result = await run.Completion;

		result.ExitCode.Should().Be(7, because: "a run that resolved its own outcome outranks the interruption");
		result.OutcomeKind.Should().NotBe(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies a run nothing ever signals fails on its timeout instead of hanging the suite. The timeout is the only thing that turns a signal that never arrived into a failing test rather than a stuck one, and it is also what makes disposal terminate.")]
	public async Task When_NoSignalEverArrives_Then_TheRunTimesOut()
	{
		await using var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(),
			options => options.RunTimeout = TimeSpan.FromMilliseconds(250));
		var run = await harness.StartRunAsync("work").ConfigureAwait(false);
		var completion = run.Completion;

#pragma warning disable VSTHRD003 // The run was started by this test, two lines up.
		var act = async () => await completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003

		await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Regression guard: verifies disposal releases process-signal ownership even when a run it is draining ended in failure. A run nobody signalled faults on its timeout, and without that being contained the exclusivity flag would stay set and the isolation never torn down — turning one failed test into every later harness in the suite refusing to start.")]
	public async Task When_ARunFailsAndTheHarnessIsDisposed_Then_OwnershipIsStillReleased()
	{
		var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(),
			options => options.RunTimeout = TimeSpan.FromMilliseconds(250));
		// Started and never signalled, so its completion faults and disposal has a failure to drain.
		_ = await harness.StartRunAsync("work").ConfigureAwait(false);

		await harness.DisposeAsync().ConfigureAwait(false);

		var next = ReplProcessSignalHarness.Create(() => CreateEchoApp());
		try
		{
			var run = await next.StartRunAsync("echo").ConfigureAwait(false);
			var completion = run.Completion;

			(await completion.ConfigureAwait(false)).ExitCode.Should().Be(
				0,
				because: "the failed run must not have stranded ownership");
		}
		finally
		{
			await next.DisposeAsync().ConfigureAwait(false);
		}
	}

	[TestMethod]
	[Description("Exercises disposal overlapping a start, which without serialisation can enumerate the run list while it is being appended to. The interleaving is not deterministic, so this is a smoke guard rather than a proof; the invariant it pins — ownership always comes back — is asserted deterministically by When_ARunFailsAndTheHarnessIsDisposed_Then_OwnershipIsStillReleased.")]
	public async Task When_DisposalRacesAStart_Then_OwnershipIsStillReleased()
	{
		var harness = ReplProcessSignalHarness.Create(
			() => CreateBlockingApp(),
			options => options.RunTimeout = TimeSpan.FromMilliseconds(250));
		try
		{
			var starting = Task.Run(async () =>
			{
				try
				{
					_ = await harness.StartRunAsync("work").ConfigureAwait(false);
				}
				catch (ObjectDisposedException)
				{
					// Losing the race to disposal is a legitimate outcome; stranding ownership is not.
				}
			});

			var disposing = harness.DisposeAsync().AsTask();
#pragma warning disable VSTHRD003 // Both tasks are started here, in this method.
			await Task.WhenAll(starting, disposing).ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		finally
		{
			await harness.DisposeAsync().ConfigureAwait(false);
		}

		// The real assertion: ownership came back, so the suite can keep going.
		var next = ReplProcessSignalHarness.Create(() => CreateEchoApp());
		try
		{
			var run = await next.StartRunAsync("echo").ConfigureAwait(false);
			var completion = run.Completion;

			(await completion.ConfigureAwait(false)).ExitCode.Should().Be(0);
		}
		finally
		{
			await next.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static ReplApp CreateBlockingApp(
		TaskCompletionSource? started = null,
		TaskCompletionSource? cleanedUp = null,
		SemaphoreSlim? cleanupGate = null,
		Action<ReplOptions>? configure = null)
	{
		var app = CreateApp(configure);
		app.Map("work", async (CancellationToken cancellationToken) =>
		{
			started?.TrySetResult();
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				return "unreachable";
			}
			finally
			{
				if (cleanupGate is not null)
				{
					await cleanupGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
				}

				cleanedUp?.TrySetResult();
			}
		});

		return app;
	}

	private static ReplApp CreateEchoApp()
	{
		var app = CreateApp(configure: null);
		app.Map("echo", () => "echoed");
		return app;
	}

	private static ReplApp CreateApp(Action<ReplOptions>? configure)
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			configure?.Invoke(options);
		});

		return app;
	}
}
