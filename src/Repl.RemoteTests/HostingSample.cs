namespace Repl.RemoteTests;

internal static class HostingSample
{
	internal static string AssemblyPath
	{
		get
		{
			var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			{
				if (File.Exists(Path.Combine(directory.FullName, "global.json")))
				{
					// Both client suites launch the executable produced by the sample project.
					return Path.Combine(directory.FullName, "samples", "05-hosting-remote", "bin", configuration,
						"net10.0", "HostingRemoteSample.dll");
				}
			}
			throw new DirectoryNotFoundException("Could not locate the repository root for the sample executable.");
		}
	}
}
