using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HostedServicesLifecycle
{
	[TestMethod]
	[Description("Regression guard: verifies default run options do not orchestrate hosted services so that external hosts can manage lifecycle.")]
	public void When_RunningWithDefaultLifecycleMode_Then_HostedServicesAreNotStartedOrStopped()
	{
		var tracker = new LifecycleTracker();
		var services = new ServiceCollection()
			.AddSingleton(tracker)
			.AddSingleton<IHostedService, TrackingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", (LifecycleTracker state) => $"{state.StartCount}/{state.StopCount}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["status", "--no-logo"], provider));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0/0");
	}

	[TestMethod]
	[Description("Regression guard: verifies internal-provider run with none lifecycle mode does not orchestrate hosted services.")]
	public void When_RunningWithInternalProviderAndNoneMode_Then_HostedServicesAreNotStartedOrStopped()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton<LifecycleTracker>();
			services.AddSingleton<IHostedService, TrackingHostedService>();
		});
		sut.Map("status", (LifecycleTracker state) => $"{state.StartCount}/{state.StopCount}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None }));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0/0");
	}

	[TestMethod]
	[Description("Regression guard: verifies host overload with none lifecycle mode does not orchestrate hosted services.")]
	public void When_RunningWithHostAndNoneMode_Then_HostedServicesAreNotStartedOrStopped()
	{
		using var host = new HostBuilder()
			.ConfigureServices(services =>
			{
				services.AddSingleton<LifecycleTracker>();
				services.AddSingleton<IHostedService, TrackingHostedService>();
			})
			.Build();
		var tracker = host.Services.GetRequiredService<LifecycleTracker>();

		var sut = ReplApp.Create();
		sut.Map("status", (LifecycleTracker state) => $"{state.StartCount}/{state.StopCount}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None }));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("0/0");
		tracker.StartCount.Should().Be(0);
		tracker.StopCount.Should().Be(0);
	}

	[TestMethod]
	[Description("Regression guard: verifies head lifecycle mode orchestrates hosted services so that start and stop are invoked around execution.")]
	public void When_RunningWithHeadLifecycleMode_Then_HostedServicesAreStartedAndStopped()
	{
		var tracker = new LifecycleTracker();
		var services = new ServiceCollection()
			.AddSingleton(tracker)
			.AddSingleton<IHostedService, TrackingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", (LifecycleTracker state) => state.StartCount);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1");
		tracker.StartCount.Should().Be(1);
		tracker.StopCount.Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: verifies internal-provider run with head lifecycle mode orchestrates hosted services around execution.")]
	public void When_RunningWithInternalProviderAndHeadMode_Then_HostedServicesAreStartedAndStopped()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton<LifecycleTracker>();
			services.AddSingleton<IHostedService, TrackingHostedService>();
		});
		sut.Map("status", (LifecycleTracker state) => state.StartCount);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1");
	}

	[TestMethod]
	[Description("Regression guard: verifies host overload with head lifecycle mode orchestrates hosted services around execution.")]
	public void When_RunningWithHostAndHeadMode_Then_HostedServicesAreStartedAndStopped()
	{
		using var host = new HostBuilder()
			.ConfigureServices(services =>
			{
				services.AddSingleton<LifecycleTracker>();
				services.AddSingleton<IHostedService, TrackingHostedService>();
			})
			.Build();
		var tracker = host.Services.GetRequiredService<LifecycleTracker>();

		var sut = ReplApp.Create();
		sut.Map("status", (LifecycleTracker state) => $"{state.StartCount}/{state.StopCount}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1/0");
		tracker.StartCount.Should().Be(1);
		tracker.StopCount.Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: verifies async host overload with head lifecycle mode orchestrates hosted services around execution.")]
	public async Task When_RunningAsyncWithHostAndHeadMode_Then_HostedServicesAreStartedAndStopped()
	{
		using var host = new HostBuilder()
			.ConfigureServices(services =>
			{
				services.AddSingleton<LifecycleTracker>();
				services.AddSingleton<IHostedService, TrackingHostedService>();
			})
			.Build();
		var tracker = host.Services.GetRequiredService<LifecycleTracker>();

		var sut = ReplApp.Create();
		sut.Map("status", (LifecycleTracker state) => $"{state.StartCount}/{state.StopCount}");

		var output = await ConsoleCaptureHelper.CaptureAsync(() => sut.RunAsync(
			["status", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head })
			.AsTask());

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("1/0");
		tracker.StartCount.Should().Be(1);
		tracker.StopCount.Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: verifies hosted service startup failures surface as execution errors on stderr so that lifecycle startup is observable without corrupting a headless run's stdout payload.")]
	public void When_HeadLifecycleStartupFails_Then_ExecutionFailsWithErrorOnStdErr()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StartFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(1);
		output.StdErr.Should().Contain("Failed to start hosted service");
		output.StdOut.Should().NotContain("Failed to start hosted service");
	}

	[TestMethod]
	[Description("Regression guard: verifies hosted service stop failures turn run into an error reported on stderr so that teardown problems are neither silent nor mixed into the stdout payload a parent process parses.")]
	public void When_HeadLifecycleStopFails_Then_ExecutionFailsWithErrorOnStdErr()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(1);
		output.StdErr.Should().Contain("Failed to stop hosted service");
		output.StdOut.Should().Be("ok" + Environment.NewLine, "the command's own payload must survive intact");
	}

	[TestMethod]
	[Description("Regression guard: verifies a hosting failure goes through the exit-code policy so that an application can publish its own code for a run that could not host its services.")]
	public void When_HeadLifecycleStopFailsAndFrameworkErrorIsRemapped_Then_ConfiguredCodeIsReturned()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.FrameworkError = 70);
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(70);
	}

	[TestMethod]
	[Description("Regression guard: verifies a run reports exactly one outcome even when hosted-service shutdown fails, so a resolver with side effects is never handed a second outcome for the same run.")]
	public void When_HeadLifecycleStopFails_Then_TheResolverObservesASingleOutcome()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var observed = new List<ReplExecutionOutcomeKind>();
		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			observed.Add(outcome.Kind);
			return outcome.ExitCode;
		});
		sut.Map("status", () => "ok");

		_ = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		observed.Should().Equal(ReplExecutionOutcomeKind.FrameworkError);
	}

	[TestMethod]
	[Description("Regression guard: verifies an already-cancelled token in head lifecycle mode follows ExitCodes.Cancelled without starting hosted services, so the policy is not bypassed by the hosting wrapper.")]
	public async Task When_HeadLifecycleTokenIsPreCancelledAndCancelledIsMapped_Then_CodeIsReturnedWithoutStartingServices()
	{
		var services = new ServiceCollection()
			.AddSingleton<LifecycleTracker>()
			.AddSingleton<IHostedService, TrackingHostedService>();
		using var provider = services.BuildServiceProvider();
		var tracker = provider.GetRequiredService<LifecycleTracker>();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.Cancelled = 130);
		sut.Map("status", () => "ok");

		var exitCode = await sut.RunAsync(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
			cts.Token);

		exitCode.Should().Be(130);
		tracker.StartCount.Should().Be(0);
		tracker.StopCount.Should().Be(0);
	}

	[TestMethod]
	[Description("Regression guard: verifies an already-cancelled token still throws in head lifecycle mode when no cancellation policy is configured, preserving the existing caller contract.")]
	public async Task When_HeadLifecycleTokenIsPreCancelledAndNoPolicyIsSet_Then_OperationCanceledExceptionPropagates()
	{
		var services = new ServiceCollection()
			.AddSingleton<LifecycleTracker>()
			.AddSingleton<IHostedService, TrackingHostedService>();
		using var provider = services.BuildServiceProvider();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		Func<Task> act = () => sut.RunAsync(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
			cts.Token).AsTask();

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a hosting failure hands the lifecycle exception to the resolver, since the coordinator wraps whatever the service threw and that is the only way a consumer can inspect it.")]
	public void When_HeadLifecycleStopFails_Then_TheOutcomeCarriesTheLifecycleException()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		ReplExecutionOutcome? observed = null;
		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.Map("status", () => "ok");

		_ = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.FrameworkError);
		observed.Exception.Should().NotBeNull();
		observed.Exception!.Message.Should().Contain("Failed to stop hosted service");
		observed.Exception.Should().NotBeOfType<AggregateException>(
			"with nothing propagating, the stop failure travels as itself");
	}

	[TestMethod]
	[Description("Regression guard: verifies a shutdown failure that outranks an exception the pipeline was propagating still carries that exception, so a failed StopAsync can no longer make an unrelated failure vanish without a rethrow, a log, or a place on the outcome.")]
	public void When_HeadLifecycleStopFailsWhileAnExceptionPropagates_Then_BothCausesTravelOnTheOutcome()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		ReplExecutionOutcome? observed = null;
		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		// A banner delegate throws outside the handler-classification path, so the pipeline propagates
		// instead of producing an outcome — the state in which the stop failure used to swallow it.
		sut.WithBanner(string () => throw new InvalidOperationException("banner boom"));
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(
			["status"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(1, "the shutdown failure still decides the code");
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.FrameworkError);

		var aggregate = observed.Exception.Should().BeOfType<AggregateException>().Subject;
		aggregate.InnerExceptions.Should().HaveCount(2);
		aggregate.InnerExceptions[0].Message.Should().Contain("Failed to stop hosted service");
		aggregate.InnerExceptions[1].Should().BeOfType<InvalidOperationException>()
			.Which.Message.Should().Be("banner boom");
		output.StdErr.Should().Contain("banner boom", "the suppressed cause must reach the operator too");
	}

	[TestMethod]
	[Description("Regression guard: verifies a startup cancelled through the caller's token is a Cancelled outcome, not a framework error: the coordinator wraps the OperationCanceledException, which used to hide it from ExitCodes.Cancelled.")]
	public async Task When_HeadLifecycleStartupIsCancelledByCaller_Then_KindIsCancelledAndCancelledCodeApplies()
	{
		using var cts = new CancellationTokenSource();
		var services = new ServiceCollection()
			.AddSingleton<CancellationTokenSource>(cts)
			.AddSingleton<IHostedService, CallerCancellingHostedService>();
		using var provider = services.BuildServiceProvider();

		ReplExecutionOutcome? observed = null;
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.ExitCodes.Cancelled = 130;
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		sut.Map("status", () => "ok");

		var exitCode = await sut.RunAsync(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
			cts.Token);

		exitCode.Should().Be(130);
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
		observed.Exception.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies a startup cancelled through the caller's token still propagates the OperationCanceledException when no cancellation policy is configured, so the head-lifecycle overload keeps the same default as every other path.")]
	public async Task When_HeadLifecycleStartupIsCancelledByCallerAndNoPolicyIsSet_Then_OperationCanceledExceptionPropagates()
	{
		using var cts = new CancellationTokenSource();
		var services = new ServiceCollection()
			.AddSingleton<CancellationTokenSource>(cts)
			.AddSingleton<IHostedService, CallerCancellingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		Func<Task> act = () => sut.RunAsync(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
			cts.Token).AsTask();

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a caller cancellation during startup does not print a hosted-service startup error, so a normal cancellation is not dressed up as a framework failure.")]
	public async Task When_HeadLifecycleStartupIsCancelledByCaller_Then_NoStartupErrorIsPrinted()
	{
		using var cts = new CancellationTokenSource();
		var services = new ServiceCollection()
			.AddSingleton<CancellationTokenSource>(cts)
			.AddSingleton<IHostedService, CallerCancellingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.Cancelled = 130);
		sut.Map("status", () => "ok");

		using var output = new StringWriter();
		int exitCode;
		using (ReplSessionIO.SetSession(output, TextReader.Null, commandOutput: output, error: output))
		{
			exitCode = await sut.RunAsync(
				["status", "--no-logo"],
				provider,
				new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
				cts.Token);
		}

		exitCode.Should().Be(130);
		output.ToString().Should().NotContain("Failed to start hosted service");
	}

	[TestMethod]
	[Description("Regression guard: verifies a shutdown failure still reaches the exit-code policy when the pipeline was propagating a cancellation, so the documented rule that a failed shutdown outranks the command outcome is not lost to the in-flight exception.")]
	public async Task When_PipelineCancelsAndShutdownAlsoFails_Then_TheShutdownFailureIsResolved()
	{
		using var cts = new CancellationTokenSource();
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Options(options => options.ExitCodes.FrameworkError = 70);
		sut.Map("work", (CancellationToken ct) =>
		{
			cts.Cancel();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});

		// No cancellation conversion configured, so the pipeline propagates an OperationCanceledException
		// through the teardown; the shutdown failure must still win and be mapped.
		var exitCode = await ConsoleCaptureHelper.CaptureAsync(() => sut.RunAsync(
			["work", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head },
			cts.Token).AsTask());

		exitCode.ExitCode.Should().Be(70);
	}

	[TestMethod]
	[Description("Regression guard: an undefined SessionScope must be rejected before hosted services start, on the hosted-lifecycle path exactly as it already is on the non-hosted one. Validating it only inside RunInSessionScopeAsync — reached after HostedServiceLifecycleCoordinator.StartAsync has already run — lets a malformed configuration value execute real startup (and then shutdown) side effects before the option error is even raised.")]
	public async Task When_TheSessionScopeIsUndefinedOnTheHostedPath_Then_HostedServicesNeverStart()
	{
		var tracker = new LifecycleTracker();
		var services = new ServiceCollection()
			.AddSingleton(tracker)
			.AddSingleton<IHostedService, TrackingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("work", () => "unreachable");

		var act = () => sut.RunAsync(
			["work", "--no-logo"],
			provider,
			new ReplRunOptions
			{
				HostedServiceLifecycle = HostedServiceLifecycleMode.Head,
				SessionScope = (SessionScopeBehavior)42,
			}).AsTask();

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false);
		tracker.StartCount.Should().Be(
			0,
			"the option error must surface before hosted services run any startup side effect");
	}

	// Cancels the caller's token from inside StartAsync, then observes it: the shape that reaches
	// ReplApp as a HostedServiceLifecycleException wrapping an OperationCanceledException.
	private sealed class CallerCancellingHostedService(CancellationTokenSource callerTokenSource) : IHostedService
	{
		public async Task StartAsync(CancellationToken cancellationToken)
		{
			await callerTokenSource.CancelAsync().ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class LifecycleTracker
	{
		public int StartCount { get; private set; }

		public int StopCount { get; private set; }

		public void OnStarted() => StartCount++;

		public void OnStopped() => StopCount++;
	}

	private sealed class TrackingHostedService(LifecycleTracker tracker) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken)
		{
			tracker.OnStarted();
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			tracker.OnStopped();
			return Task.CompletedTask;
		}
	}

	private sealed class StartFailingHostedService : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) =>
			Task.FromException(new InvalidOperationException("boom-start"));

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class StopFailingHostedService : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task StopAsync(CancellationToken cancellationToken) =>
			Task.FromException(new InvalidOperationException("boom-stop"));
	}
}
