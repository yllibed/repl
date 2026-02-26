namespace Repl.ShellCompletion;

internal sealed class ShellCompletionModule(IShellCompletionRuntime runtime) : IReplModule
{
	public void Map(IReplMap map)
	{
		if (map is not CoreReplApp)
		{
			throw new InvalidOperationException(
				"Shell completion module must be mapped at root scope.");
		}

		map.Context(ShellCompletionConstants.SetupCommandName, completion =>
		{
			completion.Map(
				ShellCompletionConstants.ProtocolSubcommandName,
				(string? shell, string? line, string? cursor) => runtime.HandleBridgeRoute(shell, line, cursor))
				.WithDescription("Internal completion bridge used by shell integrations.")
				.Hidden();
			completion.Map(
				"install",
				(string? shell, bool? force, bool? silent, CancellationToken cancellationToken) =>
					runtime.HandleInstallRouteAsync(shell, force, silent, cancellationToken))
				.WithDescription("Install shell completion into the selected shell profile.");
			completion.Map(
				"uninstall",
				(string? shell, bool? silent, CancellationToken cancellationToken) =>
					runtime.HandleUninstallRouteAsync(shell, silent, cancellationToken))
				.WithDescription("Remove shell completion from the selected shell profile.");
			completion.Map(
				"status",
				() => runtime.HandleStatusRoute())
				.WithDescription("Show completion setup status and managed profile locations.");
			completion.Map(
				"detect-shell",
				() => runtime.HandleDetectShellRoute())
				.WithDescription("Detect the current shell using environment and parent process signals.");
		})
		.Hidden();
	}
}
