namespace Repl.ShellCompletion;

internal sealed class ZshShellCompletionAdapter : IShellCompletionAdapter
{
	public static ZshShellCompletionAdapter Instance { get; } = new();

	private ZshShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.Zsh;

	public string Token => "zsh";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.ZshProfilePath))
		{
			return options.ZshProfilePath;
		}

		var zDotDir = Environment.GetEnvironmentVariable("ZDOTDIR");
		var root = string.IsNullOrWhiteSpace(zDotDir)
			? userHomePath
			: zDotDir;
		return Path.Combine(root, ".zshrc");
	}

	public string BuildManagedBlock(string commandName, string appId)
	{
		var functionName = ShellCompletionScriptBuilder.BuildShellFunctionName(commandName);
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.Zsh);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.Zsh);
		return $$"""
			{{startMarker}}
			{{functionName}}() {
			  local line cursor
			  local candidate
			  local -a reply
			  line="$BUFFER"
			  cursor=$((CURSOR > 0 ? CURSOR - 1 : 0))
			  reply=()
			  while IFS= read -r candidate; do
			    reply+=("$candidate")
			  done < <({{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell zsh --line "$line" --cursor "$cursor" --no-interactive --no-logo)
			  if (( ${#reply[@]} > 0 )); then
			    compadd -- "${reply[@]}"
			  fi
			}

			compdef {{functionName}} {{commandName}}
			{{endMarker}}
			""";
	}

	public string BuildReloadHint() =>
		"Reload your zsh profile (for example: 'source ~/.zshrc') or restart the shell to activate completions.";
}
