using DocsMcpSample;
using DocsMcpSample.Hosting;
using Microsoft.Extensions.Configuration;

// The launcher's CLI profile lets Repl own process signals; docs/architecture.md says what each one stops.
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
