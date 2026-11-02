using DocsMcpSample;
using DocsMcpSample.Hosting;
using Repl;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = lifetime.CancelAsync(); };

try
{
	var settings = Settings.FromEnvironment();

	return await CommandLine.Create(settings).RunAsync(args,
		new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None }, lifetime.Token);
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
