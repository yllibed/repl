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

	public string BuildManagedBlock(string commandName, string appId) =>
		ShellCompletionScriptBuilder.BuildBashManagedBlock(commandName, appId);

	public string BuildReloadHint() =>
		"Reload your shell profile (for example: 'source ~/.bashrc') or restart the shell to activate completions.";
}
