using System.Text.Json;
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
			using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
			connectionDeadline.CancelAfter(TimeSpan.FromSeconds(5));
			var client = await NamedPipeReplServer.ConnectAsync(args[1], connectionDeadline.Token).ConfigureAwait(false);
			await using var clientOwner = client.ConfigureAwait(false);
			if (string.Equals(args[0], "--remote-console", StringComparison.Ordinal))
			{
				return await RemoteConsole.RunAsync(client, lifetime.Token).ConfigureAwait(false);
			}
			lifetime.CancelAfter(TimeSpan.FromSeconds(30));
			await client.DiscoverAsync(lifetime.Token).ConfigureAwait(false);
			var result = await client.ExecuteAsync(new() { Arguments = args[2..] }, lifetime.Token).ConfigureAwait(false);
			await Console.Out.WriteAsync(result.StandardOutput).ConfigureAwait(false);
			await Console.Error.WriteAsync(result.StandardError).ConfigureAwait(false);
			if (result.Result is { } value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)).ConfigureAwait(false); }
			return result.ExitCode;
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
