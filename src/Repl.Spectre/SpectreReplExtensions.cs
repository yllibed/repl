using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Repl.Spectre;

/// <summary>
/// Extension methods to enable Spectre.Console integration in a Repl application.
/// </summary>
public static class SpectreReplExtensions
{
	/// <summary>
	/// Registers Spectre.Console services: <see cref="SpectreInteractionHandler"/>
	/// for rich prompts and <see cref="IAnsiConsole"/> for direct injection into commands.
	/// </summary>
	/// <param name="services">Target service collection.</param>
	/// <returns>The same service collection for chaining.</returns>
	public static IServiceCollection AddSpectreConsole(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.AddSingleton<IReplInteractionHandler, SpectreInteractionHandler>();
		services.TryAddTransient<IAnsiConsole>(_ => SessionAnsiConsole.Create());
		return services;
	}

	/// <summary>
	/// Enables Spectre.Console output rendering by registering the "spectre" output
	/// transformer and setting it as the default format. Also registers the
	/// <see cref="SpectreInteractionHandler"/> and <see cref="IAnsiConsole"/> DI service.
	/// </summary>
	/// <param name="app">Target REPL application.</param>
	/// <returns>The same app instance for chaining.</returns>
	public static ReplApp UseSpectreConsole(this ReplApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		app.Options(options =>
		{
			options.Output.AddTransformer("spectre", new SpectreHumanOutputTransformer());
			options.Output.DefaultFormat = "spectre";
		});

		return app;
	}
}
