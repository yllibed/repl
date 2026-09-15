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
	/// <para>
	/// Use <see cref="Timeout.InfiniteTimeSpan"/> to wait without a deadline. Zero and negatives are
	/// refused rather than read that way: they would put every deadline in the past, failing each wait
	/// the moment it started, which is the opposite of what a caller shortening a timeout is asking for.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">The value cannot bound a wait: it is not positive, or it is larger than the waiting primitives accept. Use <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</exception>
	public TimeSpan Timeout
	{
		get;
		set => field = ReplTestTimeout.Validated(value, ReplTestTimeout.MaxSupported, nameof(Timeout));
	} = TimeSpan.FromSeconds(30);

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
