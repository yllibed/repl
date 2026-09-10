using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

/// <summary>
/// A claimed process signal reports its interruption through the exit-code policy rather than
/// overwriting an already-resolved number, so <c>ExitCodes.Interrupted</c> and
/// <c>ExitCodes.Resolver</c> both govern a signalled run.
/// </summary>
[TestClass]
public sealed class Given_ProcessSignalExitCodePolicy
{
	[TestMethod]
	[Description("Regression guard: verifies a claimed process signal resolves through ExitCodes.Interrupted. The signal used to overwrite the run's already-resolved exit code afterwards, which left the table entry inert and handed a resolver two outcomes for one run.")]
	public async Task When_ASignalIsClaimed_Then_InterruptedGovernsTheExitCode()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var sut = CreateSignalledApp(outcome => observed = outcome, options => options.ExitCodes.Interrupted = 75);

		var exitCode = await RunWithSignalAsync(sut).ConfigureAwait(false);

		exitCode.Should().Be(75);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies an unmapped interruption keeps the conventional 128+signal code the signal carries, so wiring the policy did not change the published default for a Ctrl+C run.")]
	public async Task When_InterruptedIsUnmapped_Then_TheConventionalCodeIsKept()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var sut = CreateSignalledApp(outcome => observed = outcome);

		var exitCode = await RunWithSignalAsync(sut).ConfigureAwait(false);

		exitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies a resolver sees exactly one outcome for a signalled run. The number-overwriting form resolved the run once and then replaced its code, so a hook with side effects observed an outcome that was not the one reported.")]
	public async Task When_ASignalIsClaimed_Then_TheResolverObservesOneOutcome()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		var observed = new List<ReplExecutionOutcomeKind>();
		var sut = CreateSignalledApp(outcome => observed.Add(outcome.Kind));

		_ = await RunWithSignalAsync(sut).ConfigureAwait(false);

		observed.Should().Equal(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies a run that produced its own refusal keeps reporting it when a signal lands, rather than having a usage error replaced by the interruption code, which would hide why the command was wrong.")]
	public void When_TheRunAlreadyFailed_Then_ItKeepsItsOwnOutcome()
	{
		// The precedence rule itself, on the pure predicate that carries it. Asserted per kind rather
		// than through a race between a signal and a failing command, which no test could sequence.
		ExecutionOutcome.Success.IsInterruptible.Should().BeTrue();
		ExecutionOutcome.Help.IsInterruptible.Should().BeTrue();
		ExecutionOutcome.Cancelled(new OperationCanceledException()).IsInterruptible.Should().BeTrue();

		ExecutionOutcome.UsageError().IsInterruptible.Should().BeFalse();
		ExecutionOutcome.HandlerError(Results.Error("boom", "failed")).IsInterruptible.Should().BeFalse();
		ExecutionOutcome.HandlerException(new FormatException("boom")).IsInterruptible.Should().BeFalse();
		ExecutionOutcome.BindingError(new FormatException("boom")).IsInterruptible.Should().BeFalse();
		ExecutionOutcome.FrameworkError(rendered: null).IsInterruptible.Should().BeFalse();
	}

	[TestMethod]
	[DataRow(true, DisplayName = "with a cancellation policy configured")]
	[DataRow(false, DisplayName = "with no cancellation policy configured")]
	[Description("Regression guard: verifies a signal arriving while a hosted service is starting still reports the interruption. The coordinator wraps the cancellation in a HostedServiceLifecycleException, which used to be classified as a lifecycle failure and returned 1, and a non-zero code then defeated the signal's own.")]
	public async Task When_ASignalInterruptsHostedStartup_Then_TheInterruptionIsStillReported(bool mapCancelled)
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create(services =>
			services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, SignallingHostedService>());
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			if (mapCancelled)
			{
				options.ExitCodes.Cancelled = 66;
			}

			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => "unreachable");

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		var exitCode = await app.RunAsync(
				["work"],
				new ReplRunOptions
				{
					ProcessSignalHandling = ProcessSignalHandlingMode.Automatic,
					HostedServiceLifecycle = HostedServiceLifecycleMode.Head,
				})
			.ConfigureAwait(false);

		// The signal decides, not the lifecycle wrapper and not ExitCodes.Cancelled: the run was
		// interrupted, so it reports the conventional signal code either way.
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
		exitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
	}

	// Raises the signal from inside StartAsync, so the cancellation surfaces while the coordinator is
	// still starting services and gets wrapped in a HostedServiceLifecycleException.
	private sealed class SignallingHostedService : Microsoft.Extensions.Hosting.IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken)
		{
			_ = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[TestMethod]
	[Description("Regression guard: verifies an explicit exit code survives its payload rendering being cancelled. The result was classified after rendering, so a signal arriving mid-render lost the handler's own code and the run reported the interruption's instead, against the documented precedence.")]
	public async Task When_AnExitResultPayloadRenderIsInterrupted_Then_TheHandlerCodeStillWins()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("signalling", new SignallingTransformer());
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});

		// The handler has already decided its code; only showing the payload gets interrupted.
		app.Map("work", () => Results.Exit(3, "payload"));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		var exitCode = await app.RunAsync(
				["work", "--output:signalling"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic })
			.ConfigureAwait(false);

		exitCode.Should().Be(3);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerExitCode);
	}

	[TestMethod]
	[Description("Regression guard: verifies an exit code of zero still yields to an interruption, so the precedence covers only a code the handler actually used to report something.")]
	public async Task When_AZeroExitResultPayloadRenderIsInterrupted_Then_TheInterruptionWins()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("signalling", new SignallingTransformer());
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => Results.Exit(0, "payload"));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		var exitCode = await app.RunAsync(
				["work", "--output:signalling"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic })
			.ConfigureAwait(false);

		exitCode.Should().Be(ProcessSignalCoordinator.SigIntExitCode);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
	}

	[TestMethod]
	[Description("Regression guard: verifies an undefined ProcessSignalHandlingMode is rejected instead of falling through a negative test into automatic mode, where it would silently take process-wide signal ownership and swap the handler's token.")]
	public async Task When_TheSignalModeIsUndefined_Then_TheRunIsRejected()
	{
		var app = ReplApp.Create();
		app.Options(options => options.Interactive.InteractivePolicy = InteractivePolicy.Prevent);
		app.Map("work", () => "ok");

		Func<Task> act = () => app.RunAsync(
				["work"],
				new ReplRunOptions { ProcessSignalHandling = (ProcessSignalHandlingMode)42 })
			.AsTask();

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Regression guard: verifies a diagnostic writer that throws anything at all cannot escape a signal callback. It runs before the callback returns its suppression decision, so an escaping exception replaces cooperative cleanup with immediate process termination.")]
	public void When_TheDiagnosticWriterThrows_Then_SignalDeliveryIsUnaffected()
	{
		using var writer = new AlwaysThrowingWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		// The whole behaviour under test: this must not throw.
		ProcessSignalCoordinator.WriteDiagnostic("diagnostic that cannot be written");
	}

	// Signals only while rendering the exit result's own payload, so the cancellation lands on the
	// element that carries the code. A signal during an earlier element is a different case: the exit
	// result has not been classified yet, so reporting the interruption there is correct.
	private sealed class SignallingOnPayloadTransformer : IOutputTransformer
	{
		public string Name => "signalling";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			if (!string.Equals(value as string, "exit-payload", StringComparison.Ordinal))
			{
				return ValueTask.FromResult(string.Empty);
			}

			_ = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(string.Empty);
		}
	}

	// Raises cancellation on its own account, with nothing having asked the run to stop: a broken
	// renderer, which must not be mistaken for the handler's intentional exit.
	private sealed class SelfCancellingTransformer : IOutputTransformer
	{
		public string Name => "selfcancel";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			throw new OperationCanceledException("transformer gave up");
	}

	// Raises the signal from inside the transformer, so the cancellation surfaces while the handler's
	// payload is being rendered — after its exit code was decided.
	private sealed class SignallingTransformer : IOutputTransformer
	{
		public string Name => "signalling";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			_ = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(string.Empty);
		}
	}

	// Fails every write with something the narrow catch would not have contained.
	private sealed class AlwaysThrowingWriter : StringWriter
	{
		public override void WriteLine(string? value) => throw new InvalidOperationException("writer refuses");

		public override void Write(string? value) => throw new InvalidOperationException("writer refuses");
	}

	[TestMethod]
	[Description("Regression guard: verifies a transformer that raises cancellation on its own account, while rendering an exit result's payload, is still a failure. Keying only on the outcome kind made a broken renderer pass for an intentional exit and kept the handler's code.")]
	public void When_AnExitResultPayloadTransformerSelfCancels_Then_ItIsAFailureNotAnExit()
	{
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("selfcancel", new SelfCancellingTransformer());
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => Results.Exit(3, "payload"));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		// No signal handling and nothing cancelled: the transformer's cancellation is its own defect.
		var exitCode = app.Run(["work", "--output:selfcancel"]);

		exitCode.Should().Be(1);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
	}

	[TestMethod]
	[Description("Regression guard: verifies a tuple whose last element is an exit result keeps its code when a signal interrupts that element's rendering. The tuple renderer classified after rendering, so only the scalar path had been fixed.")]
	public async Task When_ATupleExitResultPayloadRenderIsInterrupted_Then_TheHandlerCodeStillWins()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("signalling", new SignallingOnPayloadTransformer());
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => ("first", Results.Exit(7, "exit-payload")));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		var exitCode = await app.RunAsync(
				["work", "--output:signalling"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic })
			.ConfigureAwait(false);

		exitCode.Should().Be(7);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerExitCode);
	}

	[TestMethod]
	[Description("Regression guard: verifies Mac Catalyst is named in the signal-bridge platform predicate, which this PR documents as unsupported, rather than being left to depend on whether one platform predicate implies the other.")]
	public void When_MacCatalystIsReported_Then_TheSignalBridgeIsUnsupported()
	{
		ProcessSignalCoordinator.IsSignalBridgeSupportedForTesting(
			isAndroid: false,
			isBrowser: false,
			isIOSOrMacCatalyst: true,
			isTvOS: false).Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies a caller cancellation is not swallowed by a zero exit result. Preserving every explicit code let a cancelled run exit 0, reporting success for a run that was asked to stop, with no signal handling to correct it afterwards.")]
	public async Task When_AZeroExitResultRenderIsCancelledByTheCaller_Then_CancellationWins()
	{
		using var cts = new CancellationTokenSource();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("cancelling", new CallerCancellingTransformer(cts));
			options.ExitCodes.Cancelled = 66;
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => Results.Exit(0, "payload"));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		// No signal handling: nothing downstream reclassifies a zero exit, so the predicate itself has
		// to refuse to preserve it.
		var exitCode = await app.RunAsync(
				["work", "--output:cancelling"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None },
				cts.Token)
			.ConfigureAwait(false);

		exitCode.Should().Be(66);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}

	[TestMethod]
	[Description("Regression guard: verifies a non-zero exit result still survives a caller cancellation during its payload render, so narrowing the predicate to non-zero codes did not undo the preservation it exists for.")]
	public async Task When_ANonZeroExitResultRenderIsCancelledByTheCaller_Then_TheHandlerCodeWins()
	{
		using var cts = new CancellationTokenSource();
		ReplExecutionOutcome? observed = null;
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.AddTransformer("cancelling", new CallerCancellingTransformer(cts));
			options.ExitCodes.Cancelled = 66;
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		app.Map("work", () => Results.Exit(4, "payload"));

		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);

		var exitCode = await app.RunAsync(
				["work", "--output:cancelling"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None },
				cts.Token)
			.ConfigureAwait(false);

		exitCode.Should().Be(4);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerExitCode);
	}

	[TestMethod]
	[Description("Regression guard: verifies Ctrl+Break is reported by its own name. Windows routes both keys through one console callback, which hard-coded SIGINT and so contradicted the distinction this mode documents.")]
	public async Task When_CtrlBreakIsDelivered_Then_TheDiagnosticNamesIt()
	{
		using var isolation = ProcessSignalCoordinator.IsolateRegistrationsForTesting();
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
		await using var scope = new ProcessSignalCancellationScope(default);

		var result = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting(
			specialKey: ConsoleSpecialKey.ControlBreak,
			isWindows: true);

		result.Should().Be(ConsoleCancelKeyHandlingResult.SuppressProcessTermination);
		writer.ToString().Should().Contain("Ctrl+Break").And.NotContain("Received SIGINT");
	}

	// Cancels the caller's token and observes it, so the cancellation belongs to the run rather than
	// being the transformer's own defect.
	private sealed class CallerCancellingTransformer(CancellationTokenSource cts) : IOutputTransformer
	{
		public string Name => "cancelling";

		public async ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			await cts.CancelAsync().ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return string.Empty;
		}
	}

	private static ReplApp CreateSignalledApp(
		Action<ReplExecutionOutcome> observe,
		Action<ReplOptions>? configure = null)
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Output.BannerEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			configure?.Invoke(options);
			options.ExitCodes.Resolver = outcome =>
			{
				observe(outcome);
				return outcome.ExitCode;
			};
		});

		// Raises the signal from inside the handler, which is the only in-process way to have one
		// claimed while a run is genuinely in flight.
		app.Map("work", (CancellationToken ct) =>
		{
			_ = ConsoleCancelKeyCoordinator.HandleCancelKeyForTesting();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});
		return app;
	}

	private static async ValueTask<int> RunWithSignalAsync(ReplApp sut)
	{
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
		return await sut.RunAsync(
				["work"],
				new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.Automatic })
			.ConfigureAwait(false);
	}
}
