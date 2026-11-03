using System.Text.Json;
using Repl.Remote;

namespace Repl.Samples;

internal static class RemoteClient
{
	internal static async Task<RemoteReplClient> ConnectAsync(string pipe, CancellationToken cancellationToken, TimeSpan? timeout = null)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
		try { return await NamedPipeReplServer.ConnectAsync(pipe, deadline.Token).ConfigureAwait(false); }
		catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"Could not connect to pipe '{pipe}'. Start the server first and check the pipe name and OS account.", error);
		}
	}

	internal static async Task<int> ExecuteAsync(RemoteReplClient client, string[] arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
	{
		await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
		var result = await client.ExecuteAsync(new() { Arguments = arguments }, cancellationToken).ConfigureAwait(false);
		await output.WriteAsync(result.StandardOutput).ConfigureAwait(false);
		await error.WriteAsync(result.StandardError).ConfigureAwait(false);
		if (result.Result is { } value) { await output.WriteLineAsync(JsonSerializer.Serialize(value)).ConfigureAwait(false); }
		return result.ExitCode;
	}
}
