namespace Repl.Testing;

/// <summary>
/// Options controlling the in-memory REPL test orchestrator.
/// </summary>
public sealed class ReplScenarioOptions
{
	/// <summary>
	/// Gets or sets command timeout for <see cref="ReplSessionHandle.RunCommandAsync(string, CancellationToken)"/>.
	/// </summary>
	public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Gets or sets a value indicating whether ANSI escape sequences are stripped from captured output.
	/// </summary>
	public bool NormalizeAnsi { get; set; } = true;

	/// <summary>
	/// Gets or sets a factory creating base run options for each opened session.
	/// </summary>
	public Func<ReplRunOptions> RunOptionsFactory { get; set; } = static () => new ReplRunOptions();
}
