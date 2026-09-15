using AwesomeAssertions;
using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ProcessProbe
{
	[TestMethod]
	[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
	[Description("Regression guard: verifies a real SIGTERM reaches a spawned application, which exits 143 after its cleanup ran. This is the half no in-memory test can reach: the harness proves the framework decided not to intervene a second time, only a real process proves it actually terminated with the code a shell would see.")]
	public async Task When_ARealSigTermIsDelivered_Then_TheProcessExitsAfterCleanup()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		try
		{
			await using var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options => options.Environment["REPL_TEST_SCENARIO"] = "process-signal");

			await probe.WaitForOutputAsync("READY");
			await probe.SendSignalAsync(ReplProcessSignal.Terminate);
			var exitCode = await probe.WaitForExitAsync();

			exitCode.Should().Be(143);
			(await File.ReadAllTextAsync(marker)).Should().Contain(
				"FINALLY",
				because: "a cooperative signal must give the command its cleanup, not cut the process down");
		}
		finally
		{
			File.Delete(marker);
		}
	}

	[TestMethod]
	[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
	[Description("Regression guard: verifies a real SIGINT exits a spawned application with 130, so the conventional codes are asserted against an operating system rather than against the framework's own table.")]
	public async Task When_ARealSigIntIsDelivered_Then_TheProcessExitsWith130()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		try
		{
			await using var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options => options.Environment["REPL_TEST_SCENARIO"] = "process-signal");

			await probe.WaitForOutputAsync("READY");
			await probe.SendSignalAsync(ReplProcessSignal.Interrupt);

			(await probe.WaitForExitAsync()).Should().Be(130);
		}
		finally
		{
			File.Delete(marker);
		}
	}

	[TestMethod]
	[OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
	[Description("Regression guard: verifies the probe refuses to send a signal on Windows with a message that says what to use instead, rather than appearing to deliver one. Delivering to another process there needs a console control event and console attachment, which is deliberately out of scope until issue #83 settles the Windows lifecycle.")]
	public async Task When_ASignalIsSentOnWindows_Then_ItIsRefusedWithGuidance()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		try
		{
			await using var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options => options.Environment["REPL_TEST_SCENARIO"] = "process-signal");

			// Spawning, waiting on output and killing all work here — only delivery is refused, so a
			// cross-platform suite can share everything but the signal.
			await probe.WaitForOutputAsync("READY");
			var act = async () => await probe.SendSignalAsync(ReplProcessSignal.Terminate).ConfigureAwait(false);

			await act.Should().ThrowAsync<PlatformNotSupportedException>()
				.WithMessage("*ReplProcessSignalHarness*");
		}
		finally
		{
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies Ctrl+Break is refused by the probe on every platform, because it is a Windows console event with no Unix signal the framework treats the same way. Silently mapping it to SIGQUIT would assert against a signal this framework deliberately leaves unclaimed.")]
	public async Task When_BreakIsSentToAProcess_Then_ItIsRefused()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		try
		{
			await using var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options => options.Environment["REPL_TEST_SCENARIO"] = "process-signal");
			await probe.WaitForOutputAsync("READY");

			var act = async () => await probe.SendSignalAsync(ReplProcessSignal.Break).ConfigureAwait(false);

			await act.Should().ThrowAsync<PlatformNotSupportedException>();
		}
		finally
		{
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies waiting for output the application never writes fails with what it did write, instead of timing out silently. A signal test that waits on the wrong marker is otherwise indistinguishable from one whose signal never arrived.")]
	public async Task When_TheExpectedOutputNeverArrives_Then_TheFailureCarriesWhatWasWritten()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		try
		{
			await using var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options =>
				{
					options.Environment["REPL_TEST_SCENARIO"] = "process-signal";
					options.Timeout = TimeSpan.FromSeconds(2);
				});

			var act = async () => await probe.WaitForOutputAsync("NEVER-WRITTEN").ConfigureAwait(false);

			await act.Should().ThrowAsync<TimeoutException>()
				.WithMessage("*READY*", because: "the failure must show what the process did write");
		}
		finally
		{
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies the exit code is observed while a descendant that inherited the redirected handles is still alive. Process.WaitForExitAsync ends by awaiting end-of-stream on those handles (dotnet/runtime Process.cs, WaitUntilOutputEOF), so waiting on it reports 'did not exit' for a child that exited milliseconds earlier. The probe has to watch the process, not the pipe.")]
	public async Task When_ADescendantHoldsTheHandles_Then_TheExitCodeIsStillObserved()
	{
		var (fileName, arguments) = DescendantHoldingTheHandles();

		await using var probe = ReplProcessProbe.Start(
			fileName,
			arguments,
			options => options.Timeout = TimeSpan.FromSeconds(5));

		await probe.WaitForOutputAsync("READY");

		(await probe.WaitForExitAsync()).Should().Be(7);
	}

	[TestMethod]
	[Description("Regression guard: verifies a marker written immediately before the child exits is still seen. The drain settles on end-of-stream from both readers rather than on a quiet interval, because a reader callback delayed by a busy thread pool is indistinguishable from a stream with nothing left in it.")]
	public async Task When_TheChildExitsImmediatelyAfterWriting_Then_TheMarkerIsStillSeen()
	{
		var (fileName, arguments) = EchoAndExit();

		await using var probe = ReplProcessProbe.Start(
			fileName,
			arguments,
			options => options.Timeout = TimeSpan.FromSeconds(5));

		await probe.WaitForOutputAsync("READY");

		(await probe.WaitForExitAsync()).Should().Be(3);
	}

	[TestMethod]
	[Description("Regression guard: verifies an infinite probe timeout means no deadline rather than one that has already passed. A deadline computed as now plus Timeout.InfiniteTimeSpan lands a millisecond in the past, so every wait would fail at once — the exact opposite of what the value asks for.")]
	public async Task When_TheProbeTimeoutIsInfinite_Then_WaitsStillSucceed()
	{
		var (fileName, arguments) = EchoAndExit();

		await using var probe = ReplProcessProbe.Start(
			fileName,
			arguments,
			options => options.Timeout = Timeout.InfiniteTimeSpan);

		await probe.WaitForOutputAsync("READY");

		(await probe.WaitForExitAsync()).Should().Be(3);
	}

	[TestMethod]
	[DataRow(0, DisplayName = "Zero")]
	[DataRow(-1, DisplayName = "Negative")]
	[Description("Regression guard: verifies a probe timeout that cannot bound anything is refused. Every wait on the probe promises a deadline, so a value that produces one in the past has to fail where it was set rather than where it is read.")]
	public void When_TheProbeTimeoutIsNotPositive_Then_ItIsRefused(int seconds)
	{
		var options = new ReplProcessProbeOptions();

		var act = () => options.Timeout = TimeSpan.FromSeconds(seconds);

		act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*must be positive*");
	}

	[TestMethod]
	[Description("Regression guard: verifies a null environment value removes an inherited variable from the child's environment rather than blanking it, which is what the option documents. Asserted against the child's whole environment, because a shell's 'is it defined' test cannot tell a removed variable from one set to the empty string. The paired positive control is what stops the removal assertion from passing vacuously.")]
	public async Task When_AnEnvironmentValueIsNull_Then_TheVariableIsRemovedFromTheChild()
	{
		const string Name = "REPL_PROBE_INHERITED";
		Environment.SetEnvironmentVariable(Name, "inherited");
		try
		{
			var (fileName, arguments) = PrintTheEnvironment();

			await using (var inherited = ReplProcessProbe.Start(
				fileName,
				arguments,
				options => options.Timeout = TimeSpan.FromSeconds(5)))
			{
				await inherited.WaitForExitAsync();
				inherited.Output.Should().Contain(
					$"{Name}=inherited",
					because: "the child inherits the variable when nothing removes it");
			}

			await using var removed = ReplProcessProbe.Start(
				fileName,
				arguments,
				options =>
				{
					options.Environment[Name] = null;
					options.Timeout = TimeSpan.FromSeconds(5);
				});

			await removed.WaitForExitAsync();
			removed.Output.Should().NotContain(Name);
		}
		finally
		{
			Environment.SetEnvironmentVariable(Name, value: null);
		}
	}

	// A child that exits at once, leaving a grandchild holding the redirected handles open. The hold is
	// short because nothing reaps it: the child is already gone by the time the probe is disposed, so
	// the tree walk has no root to start from — the documented limit of Kill(entireProcessTree).
	// 'timeout /t' cannot stand in for ping here: the probe closes standard input, and it refuses to run
	// without a console.
	private static (string FileName, string[] Arguments) DescendantHoldingTheHandles() =>
		OperatingSystem.IsWindows()
			? (ComSpec, ["/c", "start /b ping -n 11 127.0.0.1 & echo READY& exit 7"])
			: ("/bin/sh", ["-c", "( sleep 10 & ); echo READY; exit 7"]);

	private static (string FileName, string[] Arguments) EchoAndExit() =>
		OperatingSystem.IsWindows()
			? (ComSpec, ["/c", "echo READY& exit 3"])
			: ("/bin/sh", ["-c", "echo READY; exit 3"]);

	private static (string FileName, string[] Arguments) PrintTheEnvironment() =>
		OperatingSystem.IsWindows()
			? (ComSpec, ["/c", "set"])
			: ("/bin/sh", ["-c", "env"]);

	private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

	[TestMethod]
	[Description("Regression guard: verifies disposal kills an application still running, so a failed assertion cannot leak a blocked process into the rest of the suite.")]
	public async Task When_AProbeIsDisposedWhileRunning_Then_TheProcessIsKilled()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.marker");
		int processId;
		try
		{
			await using (var probe = ReplProcessProbe.Start(
				ShellCompletionTestHostRunner.ResolveHostExecutablePath(),
				["wait", marker],
				options => options.Environment["REPL_TEST_SCENARIO"] = "process-signal"))
			{
				await probe.WaitForOutputAsync("READY");
				processId = probe.ProcessId;
			}

			var act = () => System.Diagnostics.Process.GetProcessById(processId);

			act.Should().Throw<ArgumentException>(
				because: "the process must be gone, not merely asked to stop");
		}
		finally
		{
			File.Delete(marker);
		}
	}
}
