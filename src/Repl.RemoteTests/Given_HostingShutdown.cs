using System.Diagnostics;
using AwesomeAssertions;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_HostingShutdown
{
	[TestMethod]
	[DataRow("cooperative", 0)]
	[DataRow("async-cleanup", 1)]
	[DataRow("blocking-callback", 1)]
	[Description("The real host drains cooperative work, but exits with diagnostics within its shutdown budget even when command cleanup or cancellation callbacks never return.")]
	public async Task When_TheHostStops_Then_CommandCleanupCannotDisableItsDeadline(string behavior, int expectedExitCode)
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
			var logs = string.Join(Environment.NewLine, transcript) + await output.ConfigureAwait(false) + await error.ConfigureAwait(false);

			process.ExitCode.Should().Be(expectedExitCode, logs);
			elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), logs);
			logs.Should().Contain("Stopping administration sessions.");
			if (expectedExitCode == 0)
			{
				logs.Should().Contain("command-cleaned").And.Contain("Administration sessions stopped.")
					.And.NotContain("Administration shutdown failed");
			}
			else
			{
				logs.Should().Contain("Administration shutdown failed").And.NotContain("Administration sessions stopped.");
				if (behavior is "blocking-callback") { logs.Should().Contain("blocking-callback-entered"); }
				else { logs.Should().Contain("command-cleaning").And.NotContain("command-cleaned"); }
			}
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
}
