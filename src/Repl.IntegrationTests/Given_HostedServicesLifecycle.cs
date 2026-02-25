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
	[Description("Regression guard: verifies hosted service startup failures surface as execution errors so that lifecycle startup is observable.")]
	public void When_HeadLifecycleStartupFails_Then_ExecutionFailsWithError()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StartFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Failed to start hosted service");
	}

	[TestMethod]
	[Description("Regression guard: verifies hosted service stop failures turn run into an error so that lifecycle teardown problems are not silent.")]
	public void When_HeadLifecycleStopFails_Then_ExecutionFailsWithError()
	{
		var services = new ServiceCollection()
			.AddSingleton<IHostedService, StopFailingHostedService>();
		using var provider = services.BuildServiceProvider();

		var sut = ReplApp.Create();
		sut.Map("status", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
			["status", "--no-logo"],
			provider,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head }));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Failed to stop hosted service");
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
