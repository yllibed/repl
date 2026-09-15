namespace Repl.Testing;

/// <summary>
/// Options controlling the in-memory REPL test orchestrator.
/// </summary>
public sealed class ReplScenarioOptions
{
	/// <summary>
	/// Gets or sets command timeout for <see cref="ReplSessionHandle.RunCommandAsync(string, CancellationToken)"/>.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">The value is larger than a cancellation source can carry.</exception>
	public TimeSpan CommandTimeout
	{
		get;
		// Only the ceiling is enforced. Zero and negatives have meant "no timeout" since this shipped and
		// callers may rely on it; a value the cancellation source cannot carry has never worked at all —
		// it throws from inside RunCommandAsync, naming a 'delay' parameter the caller never passed — so
		// refusing it where it is set takes nothing away.
		set => field = value <= ReplTestTimeout.MaxSupported
			? value
			: throw new ArgumentOutOfRangeException(
				nameof(value),
				value,
				$"{nameof(CommandTimeout)} must be at most {ReplTestTimeout.MaxSupported}. Use "
				+ $"{nameof(Timeout)}.{nameof(Timeout.InfiniteTimeSpan)}, or a non-positive value, to run "
				+ "without a deadline.");
	} = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Gets or sets a value indicating whether ANSI escape sequences are stripped from captured output.
	/// </summary>
	public bool NormalizeAnsi { get; set; } = true;

	/// <summary>
	/// Gets or sets a factory creating base run options for each opened session.
	/// </summary>
	/// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
	public Func<ReplRunOptions> RunOptionsFactory
	{
		get;
		set => field = value ?? throw new ArgumentNullException(nameof(value));
	} = static () => new ReplRunOptions();
}
