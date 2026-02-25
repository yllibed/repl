namespace Repl;

/// <summary>
/// Runtime execution options for a single REPL run.
/// </summary>
public sealed record ReplRunOptions
{
	/// <summary>
	/// Gets or sets the hosted-service lifecycle behavior.
	/// </summary>
	public HostedServiceLifecycleMode HostedServiceLifecycle { get; init; } = HostedServiceLifecycleMode.None;

	/// <summary>
	/// Gets or sets the ANSI support mode for the session.
	/// </summary>
	public AnsiMode AnsiSupport { get; init; } = AnsiMode.Auto;

	/// <summary>
	/// Gets or sets optional explicit terminal metadata overrides.
	/// </summary>
	public TerminalSessionOverrides? TerminalOverrides { get; init; }
}
