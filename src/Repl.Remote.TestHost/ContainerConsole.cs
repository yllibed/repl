namespace Repl.Remote.TestHost;

internal static class ContainerConsole
{
	internal static async Task RunAsync(string container, CancellationToken ct)
	{
		string[] arguments = ["exec", "-it", container, "dotnet", "/app/HostingRemoteSample.dll", "--remote-console", "repl-hosting-sample"];
		var first = await PtySession.StartAsync("docker", arguments, ct).ConfigureAwait(false);
		await using var firstOwner = first.ConfigureAwait(false);
		var second = await PtySession.StartAsync("docker", arguments, ct).ConfigureAwait(false);
		await using var secondOwner = second.ConfigureAwait(false);
		await first.ExpectAsync("> ", ct).ConfigureAwait(false);
		await second.ExpectAsync("> ", ct).ConfigureAwait(false);
		await first.SendAsync("settings set greeting native-shared\r", ct).ConfigureAwait(false);
		await first.ExpectAsync("updated to 'native-shared'", ct).ConfigureAwait(false);
		await first.ExpectAsync("> ", ct).ConfigureAwait(false);
		await second.SendAsync("settings show greeting\r", ct).ConfigureAwait(false);
		await second.ExpectAsync("greeting = native-shared", ct).ConfigureAwait(false);
		await second.ExpectAsync("> ", ct).ConfigureAwait(false);
		await first.SendAsync("watch\r", ct).ConfigureAwait(false);
		await first.ExpectAsync("Watching...", ct).ConfigureAwait(false);
		await second.SendAsync("send native-bus-marker\r", ct).ConfigureAwait(false);
		await second.ExpectAsync("Message sent.", ct).ConfigureAwait(false);
		await second.ExpectAsync("> ", ct).ConfigureAwait(false);
		await first.ExpectAsync("native-bus-marker", ct).ConfigureAwait(false);
		await first.SendAsync("\x03", ct).ConfigureAwait(false);
		await first.ExpectAsync("Cancelled.", ct).ConfigureAwait(false);
		await first.ExpectAsync("> ", ct).ConfigureAwait(false);
		await second.SendAsync("health\r", ct).ConfigureAwait(false);
		await second.ExpectAsync("ready", ct).ConfigureAwait(false);
		await second.ExpectAsync("> ", ct).ConfigureAwait(false);
		await first.SendAsync("\x04", ct).ConfigureAwait(false);
		if (await first.WaitForExitAsync(ct).ConfigureAwait(false) != 130)
		{
			throw new InvalidOperationException("The detached console must exit with code 130.");
		}
		await second.SendAsync("watch\r", ct).ConfigureAwait(false);
		await second.ExpectAsync("Watching...", ct).ConfigureAwait(false);
		await Console.Out.WriteLineAsync("READY_FOR_STOP").ConfigureAwait(false);
		if (!string.Equals(await Console.In.ReadLineAsync(ct).ConfigureAwait(false), "stopped", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The driver did not confirm host shutdown.");
		}
		// Shutdown can reach the client as a terminal close, cancellation or transport failure.
		if (await second.WaitForExitAsync(ct).ConfigureAwait(false) is not (0 or 1 or 130))
		{
			throw new InvalidOperationException("The attached console did not handle host shutdown.");
		}
		await Console.Out.WriteLineAsync("Native console interruption, detach, shared state and host shutdown passed.").ConfigureAwait(false);
	}
}
