namespace Repl.Testing;

/// <summary>
/// Logical metadata used to shape a simulated session.
/// </summary>
public sealed record SessionDescriptor
{
	/// <summary>
	/// Optional transport name override (for example websocket, telnet, signalr).
	/// </summary>
	public string? TransportName { get; init; }

	/// <summary>
	/// Optional remote endpoint description.
	/// </summary>
	public string? RemotePeer { get; init; }

	/// <summary>
	/// Optional terminal identity override.
	/// </summary>
	public string? TerminalIdentity { get; init; }

	/// <summary>
	/// Optional terminal window size override.
	/// </summary>
	public (int Width, int Height)? WindowSize { get; init; }

	/// <summary>
	/// Optional ANSI support override.
	/// </summary>
	public bool? AnsiSupported { get; init; }

	/// <summary>
	/// Optional capability override.
	/// </summary>
	public TerminalCapabilities? TerminalCapabilities { get; init; }

	/// <summary>
	/// Optional session-specific run options customization.
	/// </summary>
	public Func<ReplRunOptions, ReplRunOptions>? ConfigureRunOptions { get; init; }

	internal ReplRunOptions BuildRunOptions(ReplScenarioOptions scenario)
	{
		ArgumentNullException.ThrowIfNull(scenario);
		var baseOptions = scenario.RunOptionsFactory();
		var overrides = baseOptions.TerminalOverrides ?? new TerminalSessionOverrides();
		overrides = overrides with
		{
			TransportName = TransportName ?? overrides.TransportName,
			RemotePeer = RemotePeer ?? overrides.RemotePeer,
			TerminalIdentity = TerminalIdentity ?? overrides.TerminalIdentity,
			WindowSize = WindowSize ?? overrides.WindowSize,
			AnsiSupported = AnsiSupported ?? overrides.AnsiSupported,
			TerminalCapabilities = TerminalCapabilities ?? overrides.TerminalCapabilities,
		};

		var configured = baseOptions with { TerminalOverrides = overrides };
		if (ConfigureRunOptions is not null)
		{
			configured = ConfigureRunOptions(configured);
		}

		return configured;
	}
}
