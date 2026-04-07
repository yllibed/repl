namespace Repl;

public sealed partial class CoreReplApp
{
	private ShellCompletionEngine? _shellCompletionEngine;
	private ShellCompletionEngine ShellCompletionEng => _shellCompletionEngine ??= new(this);

	private string[] ResolveShellCompletionCandidates(string line, int cursor) =>
		ShellCompletionEng.ResolveShellCompletionCandidates(line, cursor);

	private string ResolveShellCompletionCommandName() =>
		ShellCompletionEng.ResolveShellCompletionCommandName();

	internal static string ResolveShellCompletionCommandName(
		IReadOnlyList<string>? commandLineArgs,
		string? processPath,
		string? fallbackName) =>
		ShellCompletionEngine.ResolveShellCompletionCommandName(commandLineArgs, processPath, fallbackName);
}
