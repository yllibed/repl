using Microsoft.Extensions.DependencyInjection;
using Repl.Interaction;

namespace Repl.TerminalGui;

/// <summary>
/// Extension methods for integrating Terminal.Gui TUI hosting with Repl Toolkit.
/// </summary>
public static class TerminalGuiReplExtensions
{
	/// <summary>
	/// Registers Terminal.Gui services: <see cref="TerminalGuiInteractionHandler"/>
	/// for modal dialog prompts.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <returns>The service collection for chaining.</returns>
	public static IServiceCollection AddTerminalGui(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		services.AddSingleton<IReplInteractionHandler, TerminalGuiInteractionHandler>();

		return services;
	}
}
