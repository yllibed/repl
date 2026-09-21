namespace Repl;

/// <summary>
/// Runtime execution options for a single REPL run.
/// </summary>
public sealed record ReplRunOptions
{
	/// <summary>
	/// Gets how standalone runs handle process termination signals.
	/// <see langword="null"/> uses the active application profile's default.
	/// </summary>
	public ProcessSignalHandlingMode? ProcessSignalHandling { get; init; }

	/// <summary>
	/// Gets or sets the hosted-service lifecycle behavior.
	/// </summary>
	public HostedServiceLifecycleMode HostedServiceLifecycle { get; init; } = HostedServiceLifecycleMode.None;

	/// <summary>
	/// Gets how this run manages the session's dependency-injection scope.
	/// <see langword="null"/> resolves to <see cref="SessionScopeBehavior.PerRun"/>.
	/// </summary>
	/// <remarks>
	/// Nullable for the same reason as <see cref="ProcessSignalHandling"/>, but no profile currently sets
	/// a different default the way <see cref="ProcessSignalHandling"/>'s profiles do — every run resolves
	/// <see langword="null"/> the same way today. The nullability exists so that could change without a
	/// binary break: a composition profile can only supply its own default if "the caller said nothing"
	/// is distinguishable from an explicit value, which a non-nullable property could never express.
	/// Governs the <c>Run*</c> family only; MCP's DI scope is unconditional and has no equivalent setting.
	/// </remarks>
	public SessionScopeBehavior? SessionScope { get; init; }

	/// <summary>
	/// Gets or sets the ANSI support mode for the session.
	/// </summary>
	public AnsiMode AnsiSupport { get; init; } = AnsiMode.Auto;

	/// <summary>
	/// Gets or sets optional explicit terminal metadata overrides.
	/// </summary>
	public TerminalSessionOverrides? TerminalOverrides { get; init; }
}
