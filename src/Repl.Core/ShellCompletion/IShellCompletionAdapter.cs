namespace Repl.ShellCompletion;

internal interface IShellCompletionAdapter
{
	ShellKind Kind { get; }

	string Token { get; }

	string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath);

	string BuildManagedBlock(string commandName, string appId);

	string BuildReloadHint();
}
