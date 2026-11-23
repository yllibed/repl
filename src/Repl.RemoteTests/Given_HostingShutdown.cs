using System.Diagnostics;
using AwesomeAssertions;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_HostingShutdown
{
	// Only the critical log entry says this; the stderr report says "Administration shutdown failed: <message>".
	private const string LoggedFailure = "terminating without disposing services";
	private const string ReportedFailure = "Administration shutdown failed: ";

	[TestMethod]
	[DataRow("cooperative", 0)]
	[DataRow("async-cleanup", 1)]
	[DataRow("blocking-callback", 1)]
	[Description("The real host drains cooperative work, but exits with diagnostics within its shutdown budget even when command cleanup or cancellation callbacks never return.")]
	public async Task When_TheHostStops_Then_CommandCleanupCannotDisableItsDeadline(string behavior, int expectedExitCode)
	{
		var run = await RunHostAsync(behavior).ConfigureAwait(false);

		run.ExitCode.Should().Be(expectedExitCode, run.Logs);
		run.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), run.Logs);
		run.Output.Should().Contain("Stopping administration sessions.");
		if (expectedExitCode == 0)
		{
			run.Output.Should().Contain("command-cleaned").And.Contain("Administration sessions stopped.")
				.And.NotContain("Administration shutdown failed");
			run.Error.Should().NotContain(ReportedFailure);
		}
		else
		{
			run.Output.Should().Contain(LoggedFailure, run.Logs).And.NotContain("Administration sessions stopped.");
			run.Error.Should().Contain(ReportedFailure, run.Logs);
			if (behavior is "blocking-callback") { run.Output.Should().Contain("blocking-callback-entered"); }
			else { run.Output.Should().Contain("command-cleaning").And.NotContain("command-cleaned"); }
		}
	}

	[TestMethod]
	[Description("The console logger gives its writer only 1.5 s when disposed, so a shutdown failure logged just before the process exits can be lost when stdout lags. The failure is also reported on stderr, so an operator still sees it.")]
	public async Task When_StdoutLagsBehindAShutdownFailure_Then_StderrStillReportsIt()
	{
		var run = await RunHostAsync("slow-console-log").ConfigureAwait(false);

		run.ExitCode.Should().Be(1, run.Logs);
		run.Output.Should().NotContain(LoggedFailure, "the lagging stdout must hold the logged failure past the exit");
		run.Error.Should().Contain(ReportedFailure, run.Logs);
	}

	[TestMethod]
	[Description("The stderr report is written from a bounded thread, so a stderr that never accepts it cannot keep the process from exiting with its failure code.")]
	public async Task When_StderrBlocksTheShutdownFailureReport_Then_TheProcessStillExits()
	{
		var run = await RunHostAsync("blocked-stderr").ConfigureAwait(false);

		run.ExitCode.Should().Be(1, run.Logs);
		run.Error.Should().NotContain(ReportedFailure, "the blocked stderr must never accept the report");
		run.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), run.Logs);
		run.Output.Should().Contain(LoggedFailure, run.Logs);
	}

	private static async Task<HostRun> RunHostAsync(string behavior)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Repl.Hosting.TestHost.dll"));
		start.ArgumentList.Add(behavior);
		using var process = Process.Start(start)!;
		var transcript = new List<string>();
		var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
		Task<string>? output = null;
		try
		{
			while (await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
			{
				transcript.Add(line);
				if (line is "shutdown-test-ready") { break; }
			}
			transcript.Should().Contain("shutdown-test-ready");
			var elapsed = Stopwatch.StartNew();
			await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
			output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
			elapsed.Stop();
			var stdout = string.Join(Environment.NewLine, transcript) + Environment.NewLine + await output.ConfigureAwait(false);
			return new HostRun(process.ExitCode, elapsed.Elapsed, stdout, await error.ConfigureAwait(false));
		}
		catch (OperationCanceledException timeout) when (deadline.IsCancellationRequested)
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); }
			await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System).ConfigureAwait(false);
			var remaining = await (output ?? process.StandardOutput.ReadToEndAsync(CancellationToken.None))
				.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System).ConfigureAwait(false);
			var errors = await error.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System).ConfigureAwait(false);
			throw new TimeoutException($"Hosting shutdown ({behavior}) exceeded the test deadline.\n{string.Join(Environment.NewLine, transcript)}\n{remaining}\n{errors}", timeout);
		}
		finally
		{
			if (!process.HasExited) { process.Kill(entireProcessTree: true); }
			await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System).ConfigureAwait(false);
		}
	}

	private sealed record HostRun(int ExitCode, TimeSpan Elapsed, string Output, string Error)
	{
		public string Logs => $"stdout:{Environment.NewLine}{Output}{Environment.NewLine}stderr:{Environment.NewLine}{Error}";
	}
}
