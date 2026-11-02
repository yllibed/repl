using Repl.Remote;

namespace HostingRemoteSample;

internal static class RemoteClientMode
{
	internal static async Task<int> RunAsync(string[] args)
	{
		if (args.Length < 2)
		{
			await Console.Error.WriteLineAsync("Usage: --remote-console <pipe> | --remote-exec <pipe> <command tokens...>").ConfigureAwait(false);
			return 2;
		}
		using var lifetime = new CancellationTokenSource();
		ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
		Console.CancelKeyPress += cancel;
		try
		{
			var client = await RemoteClient.ConnectAsync(args[1], lifetime.Token).ConfigureAwait(false);
			await using var clientOwner = client.ConfigureAwait(false);
			if (string.Equals(args[0], "--remote-console", StringComparison.Ordinal))
			{
				return await RemoteConsole.RunAsync(client, lifetime.Token).ConfigureAwait(false);
			}
			lifetime.CancelAfter(TimeSpan.FromSeconds(30));
			return await RemoteClient.ExecuteAsync(client, args[2..], Console.Out, Console.Error, lifetime.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			await Console.Error.WriteLineAsync("Remote operation cancelled or timed out.").ConfigureAwait(false);
			return 130;
		}
#pragma warning disable CA1031 // The process boundary reports failures without an unhandled-exception dialog.
		catch (Exception error)
		{
			await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
			return 1;
		}
#pragma warning restore CA1031
		finally { Console.CancelKeyPress -= cancel; }
	}
}
