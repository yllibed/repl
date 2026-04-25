using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Repl.Interaction;

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
		services.TryAddSingleton<SpectreInteractionPresenter>();
		services.TryAddSingleton<IReplInteractionPresenter>(sp => sp.GetRequiredService<SpectreInteractionPresenter>());
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
	/// <param name="configure">Optional callback to configure Spectre console options.</param>
	/// <returns>The same app instance for chaining.</returns>
	public static ReplApp UseSpectreConsole(this ReplApp app, Action<SpectreConsoleOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(app);

		var spectreOptions = new SpectreConsoleOptions();
		configure?.Invoke(spectreOptions);
		SessionAnsiConsole.Options = spectreOptions;

		if (spectreOptions.Unicode && !Console.IsOutputRedirected)
		{
			Console.OutputEncoding = Encoding.UTF8;
		}

		app.Options(o =>
		{
			o.Output.AddTransformer("spectre", new SpectreHumanOutputTransformer());
			o.Output.AddHelpOutputFactory(
				"spectre",
				static (routes, contexts, scopeTokens, parsingOptions, ambientOptions) =>
					HelpTextBuilder.BuildRenderModel(
						routes,
						contexts,
						scopeTokens,
						parsingOptions,
						ambientOptions));
			if (!o.Output.TryResolveAlias("spectre", out _))
			{
				o.Output.AddAlias("spectre", "spectre");
			}
			o.Output.DefaultFormat = "spectre";
			o.Output.BannerFormats.Add("spectre");
		});

		return app;
	}
}
