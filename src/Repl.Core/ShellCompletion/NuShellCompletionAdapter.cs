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

	public string BuildManagedBlock(string commandName, string appId)
	{
		var functionName = ShellCompletionScriptBuilder.BuildShellFunctionName(commandName);
		var escapedCommandName = ShellCompletionScriptBuilder.EscapeNuDoubleQuotedLiteral(commandName);
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.Nu);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.Nu);
		return $$"""
			{{startMarker}}
			const __repl_completion_command = "{{escapedCommandName}}"
			def {{functionName}} [spans: list<string>] {
			  let line = ($spans | str join ' ')
			  let cursor = ($line | str length)
			  (
			    ^$__repl_completion_command {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell nu --line $line --cursor $cursor --no-interactive --no-logo
			    | lines
			    | each { |line| { value: $line, description: "" } }
			  )
			}

			$env.config = (
			  $env.config
			  | upsert completions.external.enable true
			  | upsert completions.external.completer { |spans| {{functionName}} $spans }
			)
			{{endMarker}}
			""";
	}

	public string BuildReloadHint() =>
		"Reload your Nushell config (for example: 'source ~/.config/nushell/config.nu') or restart the shell to activate completions.";
}
