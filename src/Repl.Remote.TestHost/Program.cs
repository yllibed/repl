using Repl.Remote.TestHost;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
try
{
	switch (args)
	{
		case ["protocol"]:
			await ProtocolClient.RunAsync(deadline.Token).ConfigureAwait(false);
			break;
		case ["container", var container]:
			await ContainerConsole.RunAsync(container, deadline.Token).ConfigureAwait(false);
			break;
		default:
			await Console.Error.WriteLineAsync("Usage: protocol | container <container-name>").ConfigureAwait(false);
			return 2;
	}
	return 0;
}
#pragma warning disable CA1031 // A test helper reports failures without opening an unhandled-exception dialog.
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.ToString()).ConfigureAwait(false);
	return 1;
}
#pragma warning restore CA1031
