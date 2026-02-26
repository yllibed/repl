namespace Repl.ShellCompletion;

internal sealed class PowerShellShellCompletionAdapter : IShellCompletionAdapter
{
	public static PowerShellShellCompletionAdapter Instance { get; } = new();

	private PowerShellShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.PowerShell;

	public string Token => "powershell";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.PowerShellProfilePath))
		{
			return options.PowerShellProfilePath;
		}

		if (OperatingSystem.IsWindows())
		{
			var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			var root = parentLooksLikeWindowsPowerShell
				? Path.Combine(documents, "WindowsPowerShell")
				: Path.Combine(documents, "PowerShell");
			return Path.Combine(root, "Microsoft.PowerShell_profile.ps1");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHomePath, ".config");
		return Path.Combine(configRoot, "powershell", "Microsoft.PowerShell_profile.ps1");
	}

	public string BuildManagedBlock(string commandName, string appId) =>
		ShellCompletionScriptBuilder.BuildPowerShellManagedBlock(commandName, appId, expectedProcessPath: Environment.ProcessPath);

	public string BuildReloadHint() =>
		"Reload your PowerShell profile (for example: '. $PROFILE') or restart the shell to activate completions.";
}
