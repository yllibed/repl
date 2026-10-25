using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Repl;

/// <summary>
/// Extension methods to register a <see cref="ReplApp"/> in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ReplServiceCollectionExtensions
{
	/// <summary>Registers a prepared application without building the caller's provider.</summary>
	/// <param name="services">Caller-owned service collection.</param>
	/// <param name="prepared">Successful preparation.</param>
	/// <param name="configureServices">Application service registration, before provider construction.</param>
	/// <param name="configure">Application mapping, with the completed host provider.</param>
	/// <returns>The collection.</returns>
	public static IServiceCollection AddRepl(this IServiceCollection services, PreparedReplStartup prepared,
		Action<IServiceCollection> configureServices, Action<IServiceProvider, ReplApp> configure)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configureServices);
		ArgumentNullException.ThrowIfNull(configure);
		var app = ReplApp.Create(prepared);
		configureServices(services);
		services.AddReplLogging();
		app.RegisterPreparedGlobals(services);
		services.TryAddSingleton(sp => { configure(sp, app); return app; });
		return services;
	}

	/// <summary>
	/// Creates and registers a singleton <see cref="ReplApp"/> in the service collection.
	/// </summary>
	/// <param name="services">Target service collection.</param>
	/// <param name="configureReplServices">Callback to register REPL-specific services.</param>
	/// <param name="configure">Callback to configure the REPL application (map commands, profiles, etc.).</param>
	/// <returns>The same service collection for chaining.</returns>
	public static IServiceCollection AddRepl(
		this IServiceCollection services,
		Action<IServiceCollection> configureReplServices,
		Action<ReplApp> configure)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configureReplServices);
		ArgumentNullException.ThrowIfNull(configure);

		services.AddReplLogging();

		var app = ReplApp.Create(configureReplServices);
		configure(app);
		services.TryAddSingleton(app);
		return services;
	}

	/// <summary>
	/// Creates and registers a singleton <see cref="ReplApp"/> in the service collection.
	/// </summary>
	/// <param name="services">Target service collection.</param>
	/// <param name="configure">Callback to configure the REPL application (map commands, profiles, etc.).</param>
	/// <returns>The same service collection for chaining.</returns>
	public static IServiceCollection AddRepl(
		this IServiceCollection services,
		Action<ReplApp> configure)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configure);

		services.AddReplLogging();

		var app = ReplApp.Create();
		configure(app);
		services.TryAddSingleton(app);
		return services;
	}

	/// <summary>
	/// Creates and registers a singleton <see cref="ReplApp"/> using deferred configuration
	/// with access to the host's <see cref="IServiceProvider"/>. This allows REPL modules to
	/// be resolved from the host's DI container.
	/// </summary>
	/// <param name="services">Target service collection.</param>
	/// <param name="configure">Callback receiving the host service provider and the REPL application.</param>
	/// <returns>The same service collection for chaining.</returns>
	public static IServiceCollection AddRepl(
		this IServiceCollection services,
		Action<IServiceProvider, ReplApp> configure)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configure);

		services.AddReplLogging();

		services.TryAddSingleton(sp =>
		{
			var app = ReplApp.Create();
			configure(sp, app);
			return app;
		});
		return services;
	}
}
