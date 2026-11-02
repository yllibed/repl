using System.Text.Json;
using Repl;
using Repl.Remote;

namespace DocsMcpSample.Hosting;

internal static class ClientMode
{
	internal static async Task<int> ConsoleAsync(Settings settings, CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(5));
		await using var client = await NamedPipeReplServer.ConnectAsync(settings.Pipe, deadline.Token);
		return await RemoteConsole.RunAsync(client, cancellationToken);
	}

	internal static async Task<int> ExecuteAsync(string[] arguments, Settings settings, IReplIoContext io, CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(5));
		await using var client = await NamedPipeReplServer.ConnectAsync(settings.Pipe, deadline.Token);
		deadline.CancelAfter(TimeSpan.FromSeconds(30));
		await client.DiscoverAsync(deadline.Token);
		var result = await client.ExecuteAsync(new() { Arguments = arguments }, deadline.Token);
		await io.Output.WriteAsync(result.StandardOutput);
		await io.Error.WriteAsync(result.StandardError);
		if (result.Result is { } value) { await io.Output.WriteLineAsync(JsonSerializer.Serialize(value)); }
		return result.ExitCode;
	}
}
