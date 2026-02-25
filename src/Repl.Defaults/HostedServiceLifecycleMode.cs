namespace Repl;

/// <summary>
/// Controls how <see cref="Microsoft.Extensions.Hosting.IHostedService"/> lifecycle is handled during a run.
/// </summary>
public enum HostedServiceLifecycleMode
{
	/// <summary>
	/// Repl does not orchestrate hosted-service lifecycle.
	/// </summary>
	None = 0,

	/// <summary>
	/// Repl runs as a component inside an existing host. Lifecycle is managed by the host.
	/// Alias of <see cref="None"/> kept for readability in hosted scenarios.
	/// </summary>
	Guest = None,

	/// <summary>
	/// Repl orchestrates hosted-service start/stop around command execution.
	/// </summary>
	Head = 1,
}
