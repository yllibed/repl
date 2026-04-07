namespace Repl;

/// <summary>
/// Provides contextual data for completion delegates.
/// </summary>
public sealed class CompletionContext
{
	/// <summary>
	/// Initializes a new instance of the <see cref="CompletionContext"/> class.
	/// </summary>
	/// <param name="services">Service provider for completion execution.</param>
	public CompletionContext(IServiceProvider services)
	{
		Services = services ?? throw new ArgumentNullException(nameof(services));
	}

	/// <summary>
	/// Gets the service provider.
	/// </summary>
	public IServiceProvider Services { get; }
}
