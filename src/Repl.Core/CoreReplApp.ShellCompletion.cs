namespace Repl;

public sealed partial class CoreReplApp
{
	private ShellCompletionEngine? _shellCompletionEngine;
	private ShellCompletionEngine ShellCompletionEng => _shellCompletionEngine ??= new(this);

	private ValueTask<string[]> ResolveShellCompletionCandidatesAsync(
		string line,
		int cursor,
		ShellCompletion.ShellKind shell,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		ShellCompletionEng.ResolveShellCompletionCandidatesAsync(line, cursor, shell, serviceProvider, cancellationToken);

	private string ResolveShellCompletionCommandName() =>
		ShellCompletionEng.ResolveShellCompletionCommandName();

	internal static string ResolveShellCompletionCommandName(
		IReadOnlyList<string>? commandLineArgs,
		string? processPath,
		string? fallbackName) =>
		ShellCompletionEngine.ResolveShellCompletionCommandName(commandLineArgs, processPath, fallbackName);
}
