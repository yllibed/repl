using System.Diagnostics;
using AwesomeAssertions;

namespace Repl.IntegrationTests;

[TestClass]
[OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
public sealed class Given_ProcessSignals
{
	private const int SigInt = 2;
	private const int SigQuit = 3;
	private const int SigTerm = 15;
	private const int SigIntExitCode = 130;
	private const int SigQuitExitCode = 131;
	private const int SigTermExitCode = 143;
	private const int CleanupDelayMilliseconds = 30_000;
	private const string CleanupCompletedMarker = "CLEANUP-COMPLETED";
	private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan ForcedTerminationMaximum = TimeSpan.FromSeconds(10);

	[TestMethod]
	[DataRow(SigInt, SigIntExitCode, "SIGINT", false, DisplayName = "RunAsync: SIGINT cancels cooperatively and exits 130")]
	[DataRow(SigTerm, SigTermExitCode, "SIGTERM", false, DisplayName = "RunAsync: SIGTERM cancels cooperatively and exits 143")]
	[DataRow(SigInt, SigIntExitCode, "SIGINT", true, DisplayName = "Run: SIGINT cancels cooperatively and exits 130")]
	[DataRow(SigTerm, SigTermExitCode, "SIGTERM", true, DisplayName = "Run: SIGTERM cancels cooperatively and exits 143")]
	[Description("A standalone one-shot run converts process signals into cooperative cancellation before exiting.")]
	public async Task When_StandaloneRunReceivesSignal_Then_FinallyRunsAndConventionalExitCodeIsReturned(
		int signal,
		int expectedExitCode,
		string expectedSignalName,
		bool useSynchronousRun)
	{
		var marker = Path.Combine(Path.GetTempPath(), $"repl-signal-{Guid.NewGuid():N}.txt");
		using var process = ShellCompletionTestHostRunner.Start(
			"process-signal",
			["wait", marker, "--no-logo"],
			out var readOutput,
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_USE_SYNC_RUN"] = useSynchronousRun.ToString(
					System.Globalization.CultureInfo.InvariantCulture),
			});
		try
		{
			await WaitForMarkerAsync(process, marker, "READY", readOutput).ConfigureAwait(false);

			await SendSignalAsync(process, signal).ConfigureAwait(false);
			await WaitForExitAsync(process, readOutput).ConfigureAwait(false);

			process.ExitCode.Should().Be(expectedExitCode);
			(await File.ReadAllLinesAsync(marker).ConfigureAwait(false)).Should().Equal("READY", "FINALLY");
			readOutput().Should().Contain(
				$"Received {expectedSignalName}; cancelling active standalone runs.");
		}
		finally
		{
			await TerminateIfRunningAsync(process).ConfigureAwait(false);
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("ProcessSignalHandlingMode.None leaves SIGTERM and cleanup ownership with the process host.")]
	public async Task When_ProcessSignalHandlingIsNone_Then_SigTermUsesOperatingSystemDefault()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"repl-signal-{Guid.NewGuid():N}.txt");
		using var process = ShellCompletionTestHostRunner.Start(
			"process-signal",
			["wait", marker, "--no-logo"],
			out var readOutput,
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SIGNAL_HANDLING"] = nameof(ProcessSignalHandlingMode.None),
			});
		try
		{
			await WaitForMarkerAsync(process, marker, "READY", readOutput).ConfigureAwait(false);

			await SendSignalAsync(process, SigTerm).ConfigureAwait(false);
			await WaitForExitAsync(process, readOutput).ConfigureAwait(false);

			process.ExitCode.Should().Be(SigTermExitCode);
			(await File.ReadAllLinesAsync(marker).ConfigureAwait(false)).Should().Equal("READY");
			readOutput().Should().NotContain("Received SIGTERM");
		}
		finally
		{
			await TerminateIfRunningAsync(process).ConfigureAwait(false);
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("Automatic handling leaves Unix SIGQUIT to the operating system instead of reinterpreting ControlBreak as SIGINT.")]
	public async Task When_AutomaticRunReceivesSigQuit_Then_OperatingSystemTerminatesProcess()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"repl-signal-{Guid.NewGuid():N}.txt");
		using var process = ShellCompletionTestHostRunner.Start(
			"process-signal",
			["wait", marker, "--no-logo"],
			out var readOutput);
		try
		{
			await WaitForMarkerAsync(process, marker, "READY", readOutput).ConfigureAwait(false);

			await SendSignalAsync(process, SigQuit).ConfigureAwait(false);
			await WaitForExitAsync(process, readOutput).ConfigureAwait(false);

			process.ExitCode.Should().Be(SigQuitExitCode);
			(await File.ReadAllLinesAsync(marker).ConfigureAwait(false)).Should().Equal("READY");
			readOutput().Should().NotContain("Received SIGINT");
		}
		finally
		{
			await TerminateIfRunningAsync(process).ConfigureAwait(false);
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("A handler's explicit non-zero exit code remains authoritative after cooperative signal cancellation.")]
	public async Task When_HandlerReturnsExplicitFailureAfterSignal_Then_HandlerExitCodeIsPreserved()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"repl-signal-{Guid.NewGuid():N}.txt");
		using var process = ShellCompletionTestHostRunner.Start(
			"process-signal-exit-code",
			["wait", marker, "--no-logo"],
			out var readOutput);
		try
		{
			await WaitForMarkerAsync(process, marker, "READY", readOutput).ConfigureAwait(false);

			await SendSignalAsync(process, SigTerm).ConfigureAwait(false);
			await WaitForExitAsync(process, readOutput).ConfigureAwait(false);

			process.ExitCode.Should().Be(7);
			(await File.ReadAllLinesAsync(marker).ConfigureAwait(false))
				.Should().Equal("READY", "HANDLER-RETURNED");
		}
		finally
		{
			await TerminateIfRunningAsync(process).ConfigureAwait(false);
			File.Delete(marker);
		}
	}

	[TestMethod]
	[Description("A second SIGTERM during cooperative cleanup promptly falls through to the operating system.")]
	public async Task When_SecondSigTermArrivesDuringCleanup_Then_OperatingSystemTerminatesProcess()
	{
		var marker = Path.Combine(Path.GetTempPath(), $"repl-signal-{Guid.NewGuid():N}.txt");
		using var process = ShellCompletionTestHostRunner.Start(
			"process-signal",
			["wait", marker, "--no-logo"],
			out var readOutput,
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SIGNAL_CLEANUP_DELAY_MS"] = CleanupDelayMilliseconds.ToString(
					System.Globalization.CultureInfo.InvariantCulture),
			});
		try
		{
			await WaitForMarkerAsync(process, marker, "READY", readOutput).ConfigureAwait(false);
			await SendSignalAsync(process, SigTerm).ConfigureAwait(false);
			await WaitForMarkerAsync(process, marker, "FINALLY", readOutput).ConfigureAwait(false);

			var forcedTermination = Stopwatch.StartNew();
			await SendSignalAsync(process, SigTerm).ConfigureAwait(false);
			await WaitForExitAsync(process, readOutput).ConfigureAwait(false);
			forcedTermination.Stop();

			process.ExitCode.Should().Be(SigTermExitCode);
			forcedTermination.Elapsed.Should().BeLessThan(ForcedTerminationMaximum);
			var markers = await File.ReadAllLinesAsync(marker).ConfigureAwait(false);
			markers.Should().Equal("READY", "FINALLY");
			markers.Should().NotContain(CleanupCompletedMarker);
			// The escalation diagnostic is documented operator-facing behavior, so pin its text: an
			// operator greps it to tell a forced termination from a crash.
			readOutput().Should().Contain("allowing immediate operating-system termination");
		}
		finally
		{
			await TerminateIfRunningAsync(process).ConfigureAwait(false);
			File.Delete(marker);
		}
	}

	private static async Task WaitForMarkerAsync(
		Process process,
		string path,
		string marker,
		Func<string> readOutput)
	{
		var deadline = DateTime.UtcNow + ProcessTimeout;
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(path)
				&& (await File.ReadAllLinesAsync(path).ConfigureAwait(false)).Contains(marker, StringComparer.Ordinal))
			{
				return;
			}

			if (process.HasExited)
			{
				throw new InvalidOperationException(
					$"Signal test host exited with code {process.ExitCode} before writing {marker}."
					+ $"{Environment.NewLine}Captured output:{Environment.NewLine}{readOutput()}");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Signal test host did not write {marker} within {ProcessTimeout}."
			+ $"{Environment.NewLine}Captured output:{Environment.NewLine}{readOutput()}");
	}

	private static async Task WaitForExitAsync(Process process, Func<string> readOutput)
	{
		try
		{
			await process.WaitForExitAsync().WaitAsync(ProcessTimeout).ConfigureAwait(false);
			process.WaitForExit();
		}
		catch (TimeoutException ex)
		{
			throw new TimeoutException(
				$"Signal test host did not exit within {ProcessTimeout}."
				+ $"{Environment.NewLine}Captured output:{Environment.NewLine}{readOutput()}",
				ex);
		}
	}

	private static async Task SendSignalAsync(Process target, int signal)
	{
		var startInfo = new ProcessStartInfo("kill")
		{
			UseShellExecute = false,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add($"-{signal}");
		startInfo.ArgumentList.Add(target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
		using var sender = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start the signal sender.");
		await sender.WaitForExitAsync().WaitAsync(ProcessTimeout).ConfigureAwait(false);
		var error = await sender.StandardError.ReadToEndAsync().ConfigureAwait(false);
		sender.ExitCode.Should().Be(0, because: $"the test signal must reach the child process: {error}");
	}

	private static async Task TerminateIfRunningAsync(Process process)
	{
		if (!process.HasExited)
		{
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync().WaitAsync(ProcessTimeout).ConfigureAwait(false);
		}
	}
}
