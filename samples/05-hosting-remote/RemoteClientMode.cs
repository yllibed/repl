using Repl;
using Repl.Parameters;
using Repl.Remote;
using Results = Repl.Results;

namespace HostingRemoteSample;

internal static class RemoteClientMode
{
	internal static ValueTask<int> RunAsync(string[] args)
	{
		var app = ReplApp.Create().UseCliProfile()
			.WithDescription("Connect to the existing hosting sample without starting another server.");
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
		});
		app.UseArgumentPreprocessor((input, _) => input.Arguments switch
		{
			["--remote-console", ..] => ArgumentPreprocessingResult.Replace(["console", .. input.Arguments.Skip(1)]),
			["--remote-exec", var pipe, ..] => ArgumentPreprocessingResult.Replace(["exec", pipe]),
			["--remote-exec"] => ArgumentPreprocessingResult.Replace(["exec"]),
			_ => ArgumentPreprocessingResult.Continue(),
		});
		// Only the destination parses the command suffix, including its help and output options.
		app.UseInvocationHook((ReplInvocationContext invocation) =>
			invocation.Parsed.Command == "exec {pipe}"
				&& invocation.Parsed.TryGetValue<string>("pipe", out var pipe)
				&& invocation.OriginalRequest.Arguments is ["--remote-exec", _, _, ..]
				? ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("exec {pipe}",
					new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						["pipe"] = pipe,
						["arguments"] = invocation.OriginalRequest.Arguments.Skip(2).ToArray(),
					}))
				: ReplInvocationResult.Continue());
		app.Map("console {pipe}", async (CancellationToken ct, string pipe) =>
			Results.Exit(await ConnectAsync(pipe, null, ct)))
			.WithDescription("Attach an interactive terminal to a named pipe.");
		app.Map("exec {pipe}", async (CancellationToken ct, string pipe, [ReplArgument] string[]? arguments = null) =>
			arguments is { Length: > 0 }
				? Results.Exit(await ConnectAsync(pipe, arguments, ct))
				: Results.Exit(2, "Supply a command after the pipe name."))
			.WithDescription("Run a command through a named pipe; following tokens belong to the server.");
		return app.RunAsync(args);
	}

	private static async Task<int> ConnectAsync(string pipe, string[]? arguments, CancellationToken cancellationToken)
	{
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		try
		{
			var client = await RemoteClient.ConnectAsync(pipe, lifetime.Token).ConfigureAwait(false);
			await using var clientOwner = client.ConfigureAwait(false);
			if (arguments is null)
			{
				return await RemoteConsole.RunAsync(client, lifetime.Token).ConfigureAwait(false);
			}
			lifetime.CancelAfter(TimeSpan.FromSeconds(30));
			return await RemoteClient.ExecuteAsync(client, arguments, Console.Out, Console.Error, lifetime.Token).ConfigureAwait(false);
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
	}
}
