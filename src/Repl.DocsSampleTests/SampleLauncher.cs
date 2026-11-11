namespace Repl.DocsSampleTests;

internal static class SampleLauncher
{
	// The sample stays on net10.0, so its launcher must run from the sample's own output, where the assemblies
	// it was built with sit next to its runtimeconfig. The test project's output holds the libraries built for
	// the test's own target framework, which a net10.0 launcher cannot load when the tests run on net11.0.
	internal static string AssemblyPath
	{
		get
		{
			var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			{
				if (File.Exists(Path.Combine(directory.FullName, "global.json")))
				{
					return Path.Combine(directory.FullName, "samples", "10-docs-mcp", "bin", configuration,
						"net10.0", "docs-index.dll");
				}
			}
			throw new DirectoryNotFoundException("Could not locate the repository root for the sample launcher.");
		}
	}
}
