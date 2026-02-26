namespace Repl.ShellCompletion;

internal sealed class FishShellCompletionAdapter : IShellCompletionAdapter
{
	public static FishShellCompletionAdapter Instance { get; } = new();

	private FishShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.Fish;

	public string Token => "fish";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.FishProfilePath))
		{
			return options.FishProfilePath;
		}

		if (OperatingSystem.IsWindows())
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var root = string.IsNullOrWhiteSpace(appData)
				? Path.Combine(userHomePath, "AppData", "Roaming")
				: appData;
			return Path.Combine(root, "fish", "config.fish");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHomePath, ".config");
		return Path.Combine(configRoot, "fish", "config.fish");
	}

	public string BuildManagedBlock(string commandName, string appId)
	{
		var functionName = ShellCompletionScriptBuilder.BuildShellFunctionName(commandName);
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.Fish);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.Fish);
		return $$"""
			{{startMarker}}
			function {{functionName}}
			  set -l line (commandline -p)
			  set -l cursor (commandline -C)
			  {{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell fish --line "$line" --cursor "$cursor" --no-interactive --no-logo
			end

			complete -c {{commandName}} -f -a "({{functionName}})"
			{{endMarker}}
			""";
	}

	public string BuildReloadHint() =>
		"Reload your fish profile (for example: 'source ~/.config/fish/config.fish') or restart the shell to activate completions.";
}
