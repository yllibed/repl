namespace Repl;

internal interface ISubInvocableReplApp
{
	ValueTask<int> RunSubInvocationAsync(
		string[] args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// As <see cref="RunSubInvocationAsync"/>, and also reports how the run ended.
	/// </summary>
	/// <param name="args">Command-line tokens for the sub-invocation.</param>
	/// <param name="serviceProvider">Resolves handler arguments.</param>
	/// <param name="cancellationToken">Cancels the run.</param>
	/// <param name="presenceServiceProvider">
	/// Decides module presence, when that must not be decided from <paramref name="serviceProvider"/> —
	/// a host that already published a catalog has to run the command the catalog promised.
	/// </param>
	ValueTask<SubInvocationOutcome> RunSubInvocationWithOutcomeAsync(
		string[] args,
		IServiceProvider serviceProvider,
		IServiceProvider? presenceServiceProvider = null,
		CancellationToken cancellationToken = default);
}
