namespace Repl.ShellCompletion;

internal interface IShellCompletionRuntime
{
	object HandleBridgeRoute(string? shell, string? line, string? cursor);

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
