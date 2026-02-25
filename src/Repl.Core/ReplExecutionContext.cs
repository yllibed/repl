namespace Repl;

/// <summary>
/// Provides execution context for middleware and handlers.
/// </summary>
public sealed class ReplExecutionContext
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ReplExecutionContext"/> class.
	/// </summary>
	/// <param name="services">Service provider used during execution.</param>
	/// <param name="cancellationToken">Execution cancellation token.</param>
	public ReplExecutionContext(IServiceProvider services, CancellationToken cancellationToken = default)
	{
		Services = services ?? throw new ArgumentNullException(nameof(services));
		CancellationToken = cancellationToken;
		Items = new Dictionary<string, object?>(StringComparer.Ordinal);
	}

	/// <summary>
	/// Gets the service provider for the current execution.
	/// </summary>
	public IServiceProvider Services { get; }

	/// <summary>
	/// Gets a mutable item bag for middleware communication.
	/// </summary>
	public IDictionary<string, object?> Items { get; }

	/// <summary>
	/// Gets the cancellation token for the current execution.
	/// </summary>
	public CancellationToken CancellationToken { get; }
}
