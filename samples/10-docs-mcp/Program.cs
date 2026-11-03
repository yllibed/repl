using DocsMcpSample;
using DocsMcpSample.Hosting;
using Microsoft.Extensions.Configuration;

// The launcher's CLI profile lets Repl own Ctrl+C, Ctrl+Break and SIGTERM: the running command is cancelled
// cooperatively, and an interrupted run exits 130 or 143.
try
{
	using var configuration = new ConfigurationManager();
	configuration.SetBasePath(Directory.GetCurrentDirectory())
		.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
		.AddEnvironmentVariables(prefix: "DOCS_");
	var settings = Settings.FromConfiguration(configuration);

	return await CommandLine.Create(settings).RunAsync(args);
}
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.ToString());
	return 1;
}
