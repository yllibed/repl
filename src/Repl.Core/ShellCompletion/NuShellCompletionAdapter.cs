namespace Repl.ShellCompletion;

internal sealed class NuShellCompletionAdapter : IShellCompletionAdapter
{
	public static NuShellCompletionAdapter Instance { get; } = new();

	private NuShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.Nu;

	public string Token => "nu";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.NuProfilePath))
		{
			return options.NuProfilePath;
		}

		if (OperatingSystem.IsWindows())
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var root = string.IsNullOrWhiteSpace(appData)
				? Path.Combine(userHomePath, "AppData", "Roaming")
				: appData;
			return Path.Combine(root, "nushell", "config.nu");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHomePath, ".config");
		return Path.Combine(configRoot, "nushell", "config.nu");
	}

	public string BuildManagedBlock(string commandName, string appId) =>
		ShellCompletionScriptBuilder.BuildNuManagedBlock(commandName, appId);

	public string BuildReloadHint() =>
		"Reload your Nushell config (for example: 'source ~/.config/nushell/config.nu') or restart the shell to activate completions.";
}
