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
