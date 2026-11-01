using Repl.Remote;

namespace DocsMcpSample.Hosting;

internal static class ServerLifetime
{
	internal static async Task<int> RunAsync(WebApplication host, NamedPipeReplServer remote)
	{
		try
		{
			await host.StartAsync();
			var stopping = Task.Delay(Timeout.InfiniteTimeSpan, host.Lifetime.ApplicationStopping);
			var ended = await Task.WhenAny(stopping, remote.Completion);
			if (ended == remote.Completion) { await remote.Completion; throw new IOException("The administration pipe stopped unexpectedly."); }
			return 0;
		}
		finally
		{
			// The deadline must be armed before synchronous host/command cancellation callbacks can run.
			try { await Task.Run(() => StopAsync(host, remote)).WaitAsync(TimeSpan.FromSeconds(15)); }
			catch (Exception error)
			{
				Log.ShutdownFailure(host.Logger, error);
				Environment.Exit(1);
			}
		}
	}

	private static async Task StopAsync(WebApplication host, NamedPipeReplServer remote)
	{
		host.Lifetime.StopApplication();
		await Task.WhenAll(remote.DisposeAsync().AsTask(), host.StopAsync());
	}
}
