using Microsoft.Extensions.DependencyInjection;

namespace Repl;

/// <summary>
/// Factory entrypoint for constructing <see cref="ReplApp"/> instances.
/// </summary>
public static class ReplAppFactory
{
	/// <summary>
	/// Creates a REPL app with an empty service collection.
	/// </summary>
	/// <returns>A new <see cref="ReplApp"/> instance.</returns>
	public static ReplApp Create() => ReplApp.Create();

	/// <summary>
	/// Creates a REPL app and configures dependency injection services.
	/// </summary>
	/// <param name="configureServices">Service registration callback.</param>
	/// <returns>A new <see cref="ReplApp"/> instance.</returns>
	public static ReplApp Create(Action<IServiceCollection> configureServices) =>
		ReplApp.Create(configureServices);
}
