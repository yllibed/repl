namespace Repl;

internal interface ISubInvocableReplApp
{
	ValueTask<int> RunSubInvocationAsync(
		string[] args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default);
}
