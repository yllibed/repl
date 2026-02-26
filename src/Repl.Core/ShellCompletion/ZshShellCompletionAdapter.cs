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

	public string BuildManagedBlock(string commandName, string appId) =>
		ShellCompletionScriptBuilder.BuildZshManagedBlock(commandName, appId);

	public string BuildReloadHint() =>
		"Reload your zsh profile (for example: 'source ~/.zshrc') or restart the shell to activate completions.";
}
