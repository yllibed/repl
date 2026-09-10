using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ProcessSignalCancellationScope
{
	[TestMethod]
	[Description("Interactive CancelKeyHandler retains Ctrl+C ownership while a standalone signal scope surrounds the run.")]
	public async Task When_InteractiveCancelHandlerIsActive_Then_StandaloneScopeDoesNotClaimCtrlC()
	{
		await using var scope = new ProcessSignalCancellationScope(default);
		using var interactiveHandler = new CancelKeyHandler();

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		result.Should().Be(ConsoleCancelKeyHandlingResult.AllowProcessTermination);
		scope.ExitCode.Should().BeNull();
		scope.Token.IsCancellationRequested.Should().BeFalse();
	}

	[TestMethod]
	[Description("Ctrl+C is routed atomically to the active interactive handler instead of merely suppressing the standalone handler.")]
	public async Task When_InteractiveCancelHandlerIsActive_Then_CtrlCIsRoutedToIt()
	{
		await using var scope = new ProcessSignalCancellationScope(default);
		using var interactiveHandler = new CancelKeyHandler();
		using var commandCancellation = new CancellationTokenSource();
		interactiveHandler.SetCommandCts(commandCancellation);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		commandCancellation.IsCancellationRequested.Should().BeTrue();
		scope.ExitCode.Should().BeNull();
		scope.Token.IsCancellationRequested.Should().BeFalse();
	}

	[TestMethod]
	[Description("The first process signal cancels every standalone scope participating in the same ownership epoch.")]
	public async Task When_FirstCtrlCArrives_Then_AllActiveScopesAreCancelled()
	{
		await using var firstScope = new ProcessSignalCancellationScope(default);
		await using var secondScope = new ProcessSignalCancellationScope(default);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		firstScope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		secondScope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		firstScope.Token.IsCancellationRequested.Should().BeTrue();
		secondScope.Token.IsCancellationRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("Ctrl+Break follows the cooperative first-signal policy on Windows.")]
	public async Task When_FirstCtrlBreakArrivesOnWindows_Then_ActiveScopeIsCancelled()
	{
		await using var scope = new ProcessSignalCancellationScope(default);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
			ConsoleSpecialKey.ControlBreak,
			isWindows: true);

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		scope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		scope.Token.IsCancellationRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("ControlBreak represents SIGQUIT on Unix and does not acquire the standalone SIGINT epoch.")]
	public async Task When_ControlBreakArrivesOnUnix_Then_StandaloneScopeDoesNotClaimSigQuit()
	{
		await using var scope = new ProcessSignalCancellationScope(default);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
			ConsoleSpecialKey.ControlBreak,
			isWindows: false);

		result.Should().Be(ConsoleCancelKeyHandlingResult.NotHandled);
		scope.ExitCode.Should().BeNull();
		scope.Token.IsCancellationRequested.Should().BeFalse();
	}

	[TestMethod]
	[Description("Disposing the interactive claim atomically hands Ctrl+C ownership back to the active standalone scope.")]
	public async Task When_InteractiveHandlerIsDisposed_Then_StandaloneScopeClaimsCtrlC()
	{
		await using var scope = new ProcessSignalCancellationScope(default);
		using var interactiveHandler = new CancelKeyHandler();
		interactiveHandler.Dispose();

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		scope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		scope.Token.IsCancellationRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("A late-joining standalone scope cannot reinterpret the process-wide second Ctrl+C as a first signal.")]
	public async Task When_ScopeJoinsAfterFirstCtrlC_Then_SecondCtrlCFallsThroughProcessWide()
	{
		await using var firstScope = new ProcessSignalCancellationScope(default);

		var firstSignal = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
		await using var lateScope = new ProcessSignalCancellationScope(default);
		var secondSignal = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		firstSignal.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		firstScope.Token.IsCancellationRequested.Should().BeTrue();
		lateScope.Token.IsCancellationRequested.Should().BeTrue();
		secondSignal.Should().Be(ConsoleCancelKeyHandlingResult.AllowProcessTermination);
	}

	[TestMethod]
	[DataRow(false, DisplayName = "Dispose old owner, then register replacement")]
	[DataRow(true, DisplayName = "Register replacement, then dispose old owner")]
	[Description("An interactive replacement registered during dispatch is reselected before standalone ownership can claim Ctrl+C.")]
	public async Task When_InteractiveOwnershipChangesDuringSelection_Then_ReplacementKeepsPriority(
		bool registerReplacementFirst)
	{
		await using var scope = new ProcessSignalCancellationScope(default);
		var initialSelectionCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseSelection = new ManualResetEventSlim();
		using var oldHandler = new CancelKeyHandler();
		using var oldCommandCancellation = new CancellationTokenSource();
		using var replacementCommandCancellation = new CancellationTokenSource();
		oldHandler.SetCommandCts(oldCommandCancellation);
		CancelKeyHandler? replacementHandler = null;
		try
		{
			var dispatchTask = Task.Run(() => ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
				ConsoleSpecialKey.ControlC,
				() =>
			{
				initialSelectionCaptured.TrySetResult();
				if (!releaseSelection.Wait(TimeSpan.FromSeconds(5)))
				{
					throw new TimeoutException("Timed out while holding the initial Ctrl+C selection.");
				}
			}));
			await initialSelectionCaptured.Task.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);

			if (registerReplacementFirst)
			{
				replacementHandler = new CancelKeyHandler();
				replacementHandler.SetCommandCts(replacementCommandCancellation);
				oldHandler.Dispose();
			}
			else
			{
				oldHandler.Dispose();
				replacementHandler = new CancelKeyHandler();
				replacementHandler.SetCommandCts(replacementCommandCancellation);
			}

			releaseSelection.Set();
			var result = await dispatchTask.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);

			result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
			oldCommandCancellation.IsCancellationRequested.Should().BeFalse();
			replacementCommandCancellation.IsCancellationRequested.Should().BeTrue();
			scope.ExitCode.Should().BeNull();
		}
		finally
		{
			releaseSelection.Set();
			replacementHandler?.Dispose();
		}
	}

	[TestMethod]
	[Description("A draining cancellation callback keeps the process epoch alive for late scopes and second-signal escalation.")]
	public async Task When_CancellationDrainIsPending_Then_LateScopeInheritsEpoch()
	{
		var firstScope = new ProcessSignalCancellationScope(default);
		ProcessSignalCancellationScope? lateScope = null;
		var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var releaseCallback = new ManualResetEventSlim();
		using var registration = firstScope.Token.Register(() =>
		{
			callbackStarted.TrySetResult();
			if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
			{
				throw new TimeoutException("Timed out while holding signal cancellation open.");
			}
		});

		try
		{
			var firstDispatchTask = Task.Run(() => ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting());
			await callbackStarted.Task.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			var disposeTask = firstScope.DisposeAsync().AsTask();
			disposeTask.IsCompleted.Should().BeFalse();

			lateScope = new ProcessSignalCancellationScope(default);
			lateScope.Token.IsCancellationRequested.Should().BeTrue();
			ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
				.Should().Be(ConsoleCancelKeyHandlingResult.AllowProcessTermination);

			releaseCallback.Set();
			await firstDispatchTask.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			await disposeTask.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		finally
		{
			releaseCallback.Set();
			await firstScope.DisposeAsync().ConfigureAwait(false);
			if (lateScope is not null)
			{
				await lateScope.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	[TestMethod]
	[Description("A cancellation callback can join the draining epoch without re-entering the process coordinator gate. This exercises the documented shape rather than proving deadlock freedom: callbacks start after both gates are released, so the callback never contends for a gate it already holds.")]
	public async Task When_CancellationCallbackStartsScope_Then_NewScopeIsCancelledWithoutDeadlock()
	{
		await using var firstScope = new ProcessSignalCancellationScope(default);
		var joinedScopeSource = new TaskCompletionSource<ProcessSignalCancellationScope>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = firstScope.Token.Register(() =>
			joinedScopeSource.TrySetResult(new ProcessSignalCancellationScope(default)));

		ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
			.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		var joinedScope = await joinedScopeSource.Task.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		await using var configuredJoinedScope = joinedScope.ConfigureAwait(false);

		joinedScope.Token.IsCancellationRequested.Should().BeTrue();
		ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
			.Should().Be(ConsoleCancelKeyHandlingResult.AllowProcessTermination);
	}

	[TestMethod]
	[Description("A scope remains signal-owned until its removal is atomic, so a signal in the pre-unregister window cannot be suppressed while the run still returns success.")]
	public async Task When_DisposalStartsBeforeAtomicUnregister_Then_SignalStillCancelsTheRun()
	{
		var scope = new ProcessSignalCancellationScope(default);
		var executionToken = scope.Token;
		var disposalPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resumeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var disposeTask = Task.Run(async () =>
			await scope.DisposeForTestingAsync(() =>
			{
			disposalPaused.SetResult();
			resumeDisposal.Task.GetAwaiter().GetResult();
		}).ConfigureAwait(false));

		await disposalPaused.Task.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		var signalResult = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
		resumeDisposal.SetResult();
		await disposeTask.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		signalResult.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		executionToken.IsCancellationRequested.Should().BeTrue();
		scope.ExitCode.Should().Be(130);
	}

	[TestMethod]
	[Description("Two concurrent process-signal dispatches produce exactly one cooperative first signal. This is a smoke test, not proof of atomicity: the coordinator gate serializes both dispatches, so the test has no interleaving control and would also pass against a non-atomic claim.")]
	public async Task When_TwoConcurrentDispatches_Then_ExactlyOneIsSuppressed()
	{
		await using var scope = new ProcessSignalCancellationScope(default);
		using var start = new ManualResetEventSlim();
		ConsoleCancelKeyHandlingResult DispatchAfterStart()
		{
			if (!start.Wait(TimeSpan.FromSeconds(5)))
			{
				throw new TimeoutException("Timed out waiting to race process signals.");
			}

			return ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
		}

		var firstDispatch = Task.Run(DispatchAfterStart);
		var secondDispatch = Task.Run(DispatchAfterStart);

		start.Set();
		var results = await Task.WhenAll(firstDispatch, secondDispatch)
			.WaitAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);

		results.Should().ContainSingle(
			result => result == ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		results.Should().ContainSingle(
			result => result == ConsoleCancelKeyHandlingResult.AllowProcessTermination);
	}

	[TestMethod]
	[Description("The process-wide signal claim resets after the final standalone scope is disposed.")]
	public async Task When_LastScopeIsDisposed_Then_NextScopeStartsANewSignalEpoch()
	{
		await using (var firstScope = new ProcessSignalCancellationScope(default))
		{
			ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
				.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		}

		await using var nextScope = new ProcessSignalCancellationScope(default);

		ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
			.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		nextScope.Token.IsCancellationRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("An explicit non-zero run result takes precedence over a claimed signal code, while a successful result uses the signal code.")]
	public async Task When_ResolvingExitCodeAfterSignal_Then_ExplicitFailureIsPreserved()
	{
		var scope = new ProcessSignalCancellationScope(default);
		ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
		await scope.DisposeAsync().ConfigureAwait(false);

		// The precedence between a claimed signal and a run's own outcome is asserted kind-by-kind in
		// Given_ProcessSignalExitCodePolicy, on the predicate that now carries it.
		scope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
	}

	[TestMethod]
	[Description("A throwing cancellation callback cannot replace the conventional signal exit policy during scope disposal.")]
	public async Task When_SignalCancellationCallbackThrows_Then_DisposalStillCompletes()
	{
		using var error = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		var scope = new ProcessSignalCancellationScope(default);
		using var registration = scope.Token.Register(
			static () => throw new InvalidOperationException("callback failure"));

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
		var act = async () => await scope.DisposeAsync().ConfigureAwait(false);

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		await act.Should().NotThrowAsync().ConfigureAwait(false);
		scope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		error.ToString().Should().Contain("process-signal cancellation callback")
			.And.Contain(nameof(InvalidOperationException));
	}

	[TestMethod]
	[Description("A failed signal registration degrades to caller-owned handling instead of aborting the run. Automatic is the CLI-profile default, so an environment that rejects a signal registration must not turn a working command into one that never executes.")]
	public async Task When_SignalRegistrationFails_Then_RunContinuesCallerOwned()
	{
		using var error = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		using var fault = ProcessSignalCoordinator.IsolateRegistrationsForTesting(
			new PlatformNotSupportedException("signal registration rejected"));

		// Construction must not throw: that is the whole behavior under test.
		await using var scope = new ProcessSignalCancellationScope(default);

		scope.Token.IsCancellationRequested.Should().BeFalse();
		scope.ExitCode.Should().BeNull();
		error.ToString().Should().Contain("Failed to install automatic process-signal handling")
			.And.Contain(nameof(PlatformNotSupportedException));
	}

	[TestMethod]
	[Description("A failed signal registration latches, so later runs in the same process do not retry it. Without the latch every subsequent run repeats a registration the environment has already refused and re-emits the same diagnostic, once per run.")]
	public async Task When_SignalRegistrationFailed_Then_LaterRunsDoNotRetry()
	{
		using var error = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		using var fault = ProcessSignalCoordinator.IsolateRegistrationsForTesting(
			new PlatformNotSupportedException("signal registration rejected"));
		await using (var first = new ProcessSignalCancellationScope(default))
		{
			first.ExitCode.Should().BeNull();
		}

		await using var second = new ProcessSignalCancellationScope(default);

		second.Token.IsCancellationRequested.Should().BeFalse();
		// Split yields one more part than there are occurrences, so a single diagnostic gives two parts.
		error.ToString().Split("Failed to install automatic process-signal handling").Should().HaveCount(2);
	}

	[TestMethod]
	[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
	[Description("A registration that fails after SIGTERM was registered disposes the orphan instead of leaking it. This is the only ordering that reaches the cleanup, and a leaked PosixSignalRegistration would keep suppressing SIGTERM for a process that has already been told the bridge is caller-owned.")]
	public async Task When_RegistrationFailsAfterSigTerm_Then_TheOrphanedRegistrationIsReleased()
	{
		using var error = new StringWriter();
		using var session = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		using (var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting(
			new PlatformNotSupportedException("cancel-key registration rejected"),
			faultAfterSigTermRegistration: true))
		{
			await using var degraded = new ProcessSignalCancellationScope(default);

			degraded.Token.IsCancellationRequested.Should().BeFalse();
			error.ToString().Should().Contain("Failed to install automatic process-signal handling");
		}

		// A leaked registration would still be claiming SIGTERM under a stale generation. After the
		// isolation scope tears down and a fresh run installs its own, signals must be claimed again.
		await using var scope = new ProcessSignalCancellationScope(default);

		ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting()
			.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
	}

	[TestMethod]
	[Description("The registration-fault test scope leaves the coordinator able to claim signals again. The scope is the only thing that resets process-wide registration state, so if it restores a registration whose captured generation is stale, every later scope in the process silently stops claiming signals.")]
	public async Task When_RegistrationFaultScopeIsDisposed_Then_SignalsAreClaimedAgain()
	{
		// Install the real registrations first: the trap only exists when the scope has something
		// to restore, which is the state every test after the first one runs in.
		await using (var warmUp = new ProcessSignalCancellationScope(default))
		{
			warmUp.ExitCode.Should().BeNull();
		}

		using (var fault = ProcessSignalCoordinator.IsolateRegistrationsForTesting(
			new PlatformNotSupportedException("signal registration rejected")))
		{
			await using var degraded = new ProcessSignalCancellationScope(default);
		}

		await using var scope = new ProcessSignalCancellationScope(default);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		scope.ExitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
	}

	[TestMethod]
	[DataRow(true, false, false, false, DisplayName = "Android")]
	[DataRow(false, true, false, false, DisplayName = "Browser")]
	[DataRow(false, false, true, false, DisplayName = "iOS family, which OperatingSystem.IsIOS also reports for Mac Catalyst")]
	[DataRow(false, false, false, true, DisplayName = "tvOS")]
	[Description("Each mobile platform flag on its own disables the signal bridge. One row per flag so a duplicated operand in the predicate cannot pass unnoticed; Mac Catalyst rides the iOS row because .NET compiles the mobile PosixSignalRegistration implementation there and OperatingSystem.IsIOS reports it.")]
	public void When_APlatformFlagIsSet_Then_SignalBridgeIsUnsupported(
		bool isAndroid,
		bool isBrowser,
		bool isIOS,
		bool isTvOS)
	{
		ProcessSignalCoordinator.IsSignalBridgeSupportedForTesting(
			isAndroid,
			isBrowser,
			isIOS,
			isTvOS).Should().BeFalse();
	}

	[TestMethod]
	[Description("A platform with no mobile flag keeps the signal bridge, so the platform predicate is not vacuously false for every input.")]
	public void When_NoPlatformFlagIsSet_Then_SignalBridgeIsSupported()
	{
		ProcessSignalCoordinator.IsSignalBridgeSupportedForTesting(
			isAndroid: false,
			isBrowser: false,
			isIOSOrMacCatalyst: false,
			isTvOS: false).Should().BeTrue();
	}

}
