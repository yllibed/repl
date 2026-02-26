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

		var processName = Path.GetFileNameWithoutExtension(processPath);
		if (!string.IsNullOrWhiteSpace(processName)
			&& string.Equals(processName, entryAssemblyName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var resolvedCommandHead = commandHead ?? ResolveCommandHead();
		var commandHeadName = Path.GetFileNameWithoutExtension(resolvedCommandHead);
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
