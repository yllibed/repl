using Repl;
using Repl.Remote;
using Repl.Samples;

namespace DocsMcpSample.Hosting;

internal static class ClientMode
{
	internal static async Task<int> ConsoleAsync(Settings settings, CancellationToken cancellationToken)
	{
		await using var client = await RemoteClient.ConnectAsync(settings.Pipe, cancellationToken);
		return await RemoteConsole.RunAsync(client, cancellationToken);
	}

	internal static async Task<int> ExecuteAsync(string[] arguments, Settings settings, IReplIoContext io, CancellationToken cancellationToken)
	{
		await using var client = await RemoteClient.ConnectAsync(settings.Pipe, cancellationToken);
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(30));
		return await RemoteClient.ExecuteAsync(client, arguments, io.Output, io.Error, deadline.Token);
	}
}
