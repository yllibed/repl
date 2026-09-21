namespace Repl;

internal interface ISubInvocableReplApp
{
	/// <summary>
	/// Runs a nested invocation against the host's own command graph, and reports how it ended.
	/// </summary>
	/// <param name="args">Command-line tokens for the sub-invocation.</param>
	/// <param name="serviceProvider">Resolves handler arguments.</param>
	/// <param name="presenceServiceProvider">
	/// Decides module presence, when that must not be decided from <paramref name="serviceProvider"/> —
	/// a host that already published a catalog has to run the command the catalog promised.
	/// </param>
	/// <param name="cancellationToken">Cancels the run.</param>
	ValueTask<SubInvocationOutcome> RunSubInvocationWithOutcomeAsync(
		string[] args,
		IServiceProvider serviceProvider,
		IServiceProvider? presenceServiceProvider = null,
		CancellationToken cancellationToken = default);
}
