using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Repl;

/// <summary>
/// Registers REPL-aware logging context services.
/// </summary>
public static class ReplLoggingServiceCollectionExtensions
{
	/// <summary>
	/// Adds REPL logging context services to the container.
	/// </summary>
	public static IServiceCollection AddReplLogging(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		services.AddLogging();
		services.TryAddSingleton<IReplLogContextAccessor, LiveReplLogContextAccessor>();
		return services;
	}
}
