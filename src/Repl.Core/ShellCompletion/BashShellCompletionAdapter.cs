namespace Repl.ShellCompletion;

internal sealed class BashShellCompletionAdapter : IShellCompletionAdapter
{
	public static BashShellCompletionAdapter Instance { get; } = new();

	private BashShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.Bash;

	public string Token => "bash";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.BashProfilePath))
		{
			return options.BashProfilePath;
		}

		var bashRc = Path.Combine(userHomePath, ".bashrc");
		var bashProfile = Path.Combine(userHomePath, ".bash_profile");
		return File.Exists(bashRc) || !File.Exists(bashProfile)
			? bashRc
			: bashProfile;
	}

	public string BuildManagedBlock(string commandName, string appId)
	{
		var functionName = ShellCompletionScriptBuilder.BuildShellFunctionName(commandName);
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.Bash);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.Bash);
		return $$"""
			{{startMarker}}
			{{functionName}}() {
			  local line cursor
			  local candidate
			  line="$COMP_LINE"
			  cursor="$COMP_POINT"
			  COMPREPLY=()
			  while IFS= read -r candidate; do
			    COMPREPLY+=("$candidate")
			  done < <({{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell bash --line "$line" --cursor "$cursor" --no-interactive --no-logo)
			}

			complete -F {{functionName}} {{commandName}}
			{{endMarker}}
			""";
	}

	public string BuildReloadHint() =>
		"Reload your shell profile (for example: 'source ~/.bashrc') or restart the shell to activate completions.";
}
