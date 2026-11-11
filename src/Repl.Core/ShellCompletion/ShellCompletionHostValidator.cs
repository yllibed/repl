namespace Repl;

internal static class ShellCompletionHostValidator
{
	internal const string UnsupportedHostMessage =
		"Shell completion setup requires running the REPL through its own executable command (the CLI head must match the current app binary).";

	internal static bool IsSupportedHostProcess(
		string? processPath,
		string? entryAssemblyName,
		string? commandHead = null,
		string? parentProcessName = null)
	{
		if (string.IsNullOrWhiteSpace(entryAssemblyName))
		{
			return false;
		}

		var processName = GetExecutableName(processPath);
		if (!string.IsNullOrWhiteSpace(processName)
			&& string.Equals(processName, entryAssemblyName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var resolvedCommandHead = commandHead ?? ResolveCommandHead();
		var commandHeadName = GetExecutableName(resolvedCommandHead);
		if (string.IsNullOrWhiteSpace(commandHeadName)
			|| !string.Equals(commandHeadName, entryAssemblyName, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(processName))
		{
			return true;
		}

		var resolvedParentProcessName = parentProcessName ?? ResolveParentProcessName();
		return string.IsNullOrWhiteSpace(resolvedParentProcessName)
			|| !string.Equals(processName, resolvedParentProcessName, StringComparison.OrdinalIgnoreCase);
	}

	// Drops only an executable extension. Path.GetFileNameWithoutExtension reads the last dot-separated part of any
	// name as an extension, so "Acme.Tool" would become "Acme"; and .NET 11 reports the extensionless apphost
	// as the command head where .NET 10 reported the dll, which made that mismatch decisive.
	private static string GetExecutableName(string? path)
	{
		var name = Path.GetFileName(path);
		if (string.IsNullOrEmpty(name))
		{
			return string.Empty;
		}

		return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
			? name[..^4]
			: name;
	}

	private static string ResolveCommandHead()
	{
		try
		{
			var args = Environment.GetCommandLineArgs();
			return args.Length > 0 ? args[0] : string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string ResolveParentProcessName()
	{
		return string.Empty;
	}
}
