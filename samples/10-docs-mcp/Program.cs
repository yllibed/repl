using DocsMcpSample;
using DocsMcpSample.Hosting;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = lifetime.CancelAsync(); };

try
{
	var settings = Settings.FromEnvironment();

	return args is [] or ["serve"]
		? await Server.RunAsync(settings, lifetime.Token)
		: await ClientMode.RunAsync(args, settings, lifetime.Token);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
{
	return 130;
}
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.Message);
	return 1;
}
