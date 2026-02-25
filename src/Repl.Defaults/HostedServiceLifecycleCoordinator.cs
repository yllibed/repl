using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Repl;

internal static class HostedServiceLifecycleCoordinator
{
	public static async ValueTask<IReadOnlyList<IHostedService>> StartAsync(
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();
		if (hostedServices.Length == 0)
		{
			return [];
		}

		var started = new List<IHostedService>(hostedServices.Length);
		foreach (var hostedService in hostedServices)
		{
			try
			{
				await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);
				started.Add(hostedService);
			}
			catch (Exception ex)
			{
				await StopIgnoringErrorsAsync(started, CancellationToken.None).ConfigureAwait(false);
				throw new HostedServiceLifecycleException(
					$"Failed to start hosted service '{hostedService.GetType().Name}'.",
					ex);
			}
		}

		return started;
	}

	public static async ValueTask StopAsync(
		IReadOnlyList<IHostedService> startedServices,
		CancellationToken cancellationToken)
	{
		for (var index = startedServices.Count - 1; index >= 0; index--)
		{
			var hostedService = startedServices[index];
			try
			{
				await hostedService.StopAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				throw new HostedServiceLifecycleException(
					$"Failed to stop hosted service '{hostedService.GetType().Name}'.",
					ex);
			}
		}
	}

	private static async ValueTask StopIgnoringErrorsAsync(
		List<IHostedService> startedServices,
		CancellationToken cancellationToken)
	{
		for (var index = startedServices.Count - 1; index >= 0; index--)
		{
			try
			{
				await startedServices[index].StopAsync(cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				// Best-effort cleanup when startup fails; preserve original startup error.
			}
		}
	}
}
