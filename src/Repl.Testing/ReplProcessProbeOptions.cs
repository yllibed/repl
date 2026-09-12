namespace Repl.Testing;

/// <summary>
/// Options for one <see cref="ReplProcessProbe"/>.
/// </summary>
public sealed class ReplProcessProbeOptions
{
	/// <summary>
	/// How long any single wait may take — for expected output, for a signal to be delivered, or for
	/// the process to exit — before it fails with a <see cref="TimeoutException"/> carrying everything
	/// captured so far. Defaults to 30 seconds.
	/// </summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Environment variables to set on the child, on top of the ones it inherits. A
	/// <see langword="null"/> value removes an inherited variable.
	/// </summary>
	public IDictionary<string, string?> Environment { get; } =
		new Dictionary<string, string?>(StringComparer.Ordinal);

	/// <summary>
	/// The child's working directory. <see langword="null"/> inherits the current one.
	/// </summary>
	public string? WorkingDirectory { get; set; }
}
