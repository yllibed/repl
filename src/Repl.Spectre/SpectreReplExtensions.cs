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
	/// In an externally managed container (the <c>AddRepl</c> pattern), the registered
	/// services resolve the app's terminal detection through the <see cref="ReplApp"/>
	/// registered in that container; an explicit options registration in the container
	/// always wins, and a container holding several apps should register options explicitly.
	/// </summary>
	/// <param name="services">Target service collection.</param>
	/// <returns>The same service collection for chaining.</returns>
	public static IServiceCollection AddSpectreConsole(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.TryAddSingleton(sp => new SpectreInteractionPresenter(
			ResolveInteractionOptions(sp) ?? new InteractionOptions(),
			ResolveOutputOptions(sp) ?? new OutputOptions()));
		services.TryAddSingleton<IReplInteractionPresenter>(sp => sp.GetRequiredService<SpectreInteractionPresenter>());
		// The generic factory overload keeps the implementation type on the descriptor, which
		// TryAddEnumerable needs for dedup — a second AddSpectreConsole call must not register
		// a duplicate handler.
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IReplInteractionHandler, SpectreInteractionHandler>(sp =>
			new SpectreInteractionHandler(ResolveOutputOptions(sp), ResolveSpectreOptions(sp))));
		// The container carries the app's OutputOptions (registered by ReplApp), so the
		// injected console follows the host's terminal detection even when
		// UseSpectreConsole was not called.
		services.TryAddTransient<IAnsiConsole>(sp =>
			SessionAnsiConsole.Create(ResolveOutputOptions(sp), ResolveSpectreOptions(sp)));
		return services;
	}

	// Externally managed DI (the AddRepl pattern): the host's container does not carry the
	// framework-registered options — those live in the ReplApp's own service collection —
	// but it does carry the ReplApp itself. Deferring to the app's container keeps the host
	// terminal detection and Spectre options flowing instead of silently reverting injected
	// consoles and prompts to Spectre-side detection. Precedence: an explicit registration
	// in the resolving container always wins over the app fallback. A container holding
	// several ReplApps resolves the last-registered one — register per-app options
	// explicitly in that (unusual) setup.
	private static OutputOptions? ResolveOutputOptions(IServiceProvider sp) =>
		sp.GetService<OutputOptions>() ?? sp.GetService<ReplApp>()?.Services.GetService<OutputOptions>();

	private static InteractionOptions? ResolveInteractionOptions(IServiceProvider sp) =>
		sp.GetService<InteractionOptions>() ?? sp.GetService<ReplApp>()?.Services.GetService<InteractionOptions>();

	// SpectreConsoleOptions comes from the app's extension-state bag, not its provider:
	// UseSpectreConsole may run after the shared provider was materialized, where a late
	// service descriptor would be silently invisible.
	private static SpectreConsoleOptions? ResolveSpectreOptions(IServiceProvider sp) =>
		sp.GetService<SpectreConsoleOptions>() ?? sp.GetService<ReplApp>()?.GetExtensionState<SpectreConsoleOptions>();

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
		// Per-app options flow through the app's extension-state bag (interaction handler,
		// injected consoles) and the transformer parameter below — no process-wide static,
		// so parallel apps cannot contaminate each other's Spectre configuration; the bag
		// (unlike a late service descriptor) also survives a shared provider that was
		// already materialized by MapModule<T>()/app.Services.
		app.SetExtensionState(spectreOptions);

		if (spectreOptions.Unicode && !Console.IsOutputRedirected)
		{
			Console.OutputEncoding = Encoding.UTF8;
		}

		app.Options(o =>
		{
			o.Output.AddTransformer("spectre", new SpectreHumanOutputTransformer(o.Output.ResolveHumanRenderSettings, o.Output, spectreOptions));
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
