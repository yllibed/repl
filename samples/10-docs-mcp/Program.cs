using DocsMcpSample;
using DocsMcpSample.Hosting;
using Microsoft.Extensions.Configuration;
using Repl;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = lifetime.CancelAsync(); };

try
{
	using var configuration = new ConfigurationManager();
	configuration.SetBasePath(Directory.GetCurrentDirectory())
		.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
		.AddEnvironmentVariables(prefix: "DOCS_");
	var settings = Settings.FromConfiguration(configuration);

	return await CommandLine.Create(settings).RunAsync(args,
		new ReplRunOptions { ProcessSignalHandling = ProcessSignalHandlingMode.None }, lifetime.Token);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
{
	return 130;
}
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.ToString());
	return 1;
}
