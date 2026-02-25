namespace Repl;

internal static class ShellCompletionHostValidator
{
	internal const string UnsupportedHostMessage =
		"Shell completion setup requires running the REPL through its own executable command (the CLI head must match the current app binary).";

	internal static bool IsSupportedHostProcess(
		string? processPath,
		string? entryAssemblyName)
	{
		if (string.IsNullOrWhiteSpace(processPath))
		{
			return false;
		}

		var fileName = Path.GetFileNameWithoutExtension(processPath);
		return !string.IsNullOrWhiteSpace(entryAssemblyName)
			&& string.Equals(fileName, entryAssemblyName, StringComparison.OrdinalIgnoreCase);
	}
}
