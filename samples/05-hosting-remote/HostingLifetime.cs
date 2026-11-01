using Repl.Remote;

namespace HostingRemoteSample;

internal static class HostingLifetime
{
	private static readonly Action<ILogger, Exception?> Started = LoggerMessage.Define(LogLevel.Information, eventId: default, "Administration host started.");
	private static readonly Action<ILogger, Exception?> Stopping = LoggerMessage.Define(LogLevel.Information, eventId: default, "Stopping administration sessions.");
	private static readonly Action<ILogger, Exception?> Stopped = LoggerMessage.Define(LogLevel.Information, eventId: default, "Administration sessions stopped.");
	private static readonly Action<ILogger, Exception?> StartupFailed = LoggerMessage.Define(LogLevel.Error, eventId: default, "Administration host failed.");
	private static readonly Action<ILogger, Exception?> ShutdownFailed = LoggerMessage.Define(LogLevel.Critical, eventId: default, "Administration shutdown failed; terminating without disposing services beneath active work.");

	internal static async Task<int> RunAsync(WebApplication host, NamedPipeReplServer listener)
	{
		var exitCode = 0;
		try
		{
			await host.StartAsync().ConfigureAwait(false);
			Started(host.Logger, null);
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, host.Lifetime.ApplicationStopping).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (host.Lifetime.ApplicationStopping.IsCancellationRequested) { }
		}
#pragma warning disable CA1031 // This executable reports startup failures with logs and a process exit code.
		catch (Exception error) { StartupFailed(host.Logger, error); exitCode = 1; }
#pragma warning restore CA1031
		finally
		{
			// Arm the deadline before cleanup can enter synchronous application callbacks.
			try { await Task.Run(() => StopAsync(host, listener)).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
#pragma warning disable CA1031 // Timed-out or failed cleanup cannot safely release shared providers used by active work.
			catch (Exception error)
			{
				ShutdownFailed(host.Logger, error);
				Environment.Exit(1);
			}
#pragma warning restore CA1031
		}
		return exitCode;
	}

	private static async Task StopAsync(WebApplication host, NamedPipeReplServer listener)
	{
		host.Lifetime.StopApplication();
		Stopping(host.Logger, null);
		try
		{
			await Task.WhenAll(listener.DisposeAsync().AsTask(), host.Services.GetRequiredService<SignalRSessions>().DrainAsync()).ConfigureAwait(false);
		}
		finally { await host.StopAsync().ConfigureAwait(false); }
		Stopped(host.Logger, null);
	}
}
