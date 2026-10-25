namespace Repl;

internal sealed class StartupInvocation(ProcessedInput input)
{
	internal ProcessedInput Input { get; set; } = input;
	internal Exception? Failure { get; set; }
}
