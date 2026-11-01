using System.Runtime.InteropServices;
using System.Text.Json;
using Repl.Remote;

namespace DocsMcpSample.Hosting;

internal static class ClientMode
{
	internal static async Task<int> RunAsync(string[] args, Settings settings, CancellationToken cancellationToken)
	{
		if (args is ["assets", "fetch", ..] && args.Length <= 3)
		{
			await Assets.FetchAsync(settings.Assets, args.Length == 3 ? args[2] : RuntimeInformation.RuntimeIdentifier, cancellationToken);
			return 0;
		}
		if (args is not (["console"] or ["health"] or ["health", "--ready"] or ["exec", _, ..]))
		{
			await Console.Error.WriteLineAsync("Usage: docs-index [serve | console | exec <tokens...> | health [--ready] | assets fetch [rid]]");
			return 2;
		}
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(5));
		await using var client = await NamedPipeReplServer.ConnectAsync(settings.Pipe, deadline.Token);
		if (args[0] == "console") { return await RemoteConsole.RunAsync(client, cancellationToken); }
		deadline.CancelAfter(TimeSpan.FromSeconds(30));
		await client.DiscoverAsync(deadline.Token);
		var arguments = args[0] == "health" ? args : args[1..];
		var result = await client.ExecuteAsync(new() { Arguments = arguments }, deadline.Token);
		await Console.Out.WriteAsync(result.StandardOutput);
		await Console.Error.WriteAsync(result.StandardError);
		if (result.Result is { } value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); }
		return result.ExitCode;
	}
}
