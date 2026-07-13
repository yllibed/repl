namespace Repl.ShellCompletion;

internal interface IShellCompletionRuntime
{
	ValueTask<object> HandleBridgeRouteAsync(
		string? shell,
		string? line,
		string? cursor,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken);

	object HandleStatusRoute();

	object HandleDetectShellRoute();

	ValueTask<object> HandleInstallRouteAsync(
		string? shell,
		bool? force,
		bool? silent,
		CancellationToken cancellationToken);

	ValueTask<object> HandleUninstallRouteAsync(
		string? shell,
		bool? silent,
		CancellationToken cancellationToken);

	ValueTask HandleStartupAsync(
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken);
}
