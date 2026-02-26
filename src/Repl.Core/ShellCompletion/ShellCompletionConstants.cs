namespace Repl.ShellCompletion;

internal static class ShellCompletionConstants
{
	public const string SetupCommandName = "completion";
	public const string ProtocolSubcommandName = "__complete";
	public const string BridgeCommandName = SetupCommandName + " " + ProtocolSubcommandName;
	public const string Usage =
		"usage: completion __complete --shell <bash|powershell|zsh> --line <input> --cursor <position>";
	public const string ManagedBlockStartPrefix = "# >>> repl completion [";
	public const string ManagedBlockEndPrefix = "# <<< repl completion [";
	public const string ManagedBlockStartSuffix = "] >>>";
	public const string ManagedBlockEndSuffix = "] <<<";
	public const string StateFileName = "shell-completion-state.json";
}
