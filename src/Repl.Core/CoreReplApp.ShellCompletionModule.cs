namespace Repl;

public sealed partial class CoreReplApp
{
	private sealed class ShellCompletionModule(CoreReplApp app) : IReplModule
	{
		public void Map(IReplMap map)
		{
			if (!ReferenceEquals(map, app))
			{
				throw new InvalidOperationException(
					"Shell completion module must be mapped at root scope.");
			}

			map.Context(ShellCompletionSetupCommandName, completion =>
			{
				completion.Map(
					ShellCompletionProtocolSubcommandName,
					(string? shell, string? line, string? cursor) => app.HandleShellCompletionBridgeRoute(shell, line, cursor))
					.WithDescription("Internal completion bridge used by shell integrations.")
					.Hidden();
				completion.Map(
					"install",
					(string? shell, bool? force, bool? silent, CancellationToken cancellationToken) =>
						app.HandleShellCompletionInstallRouteAsync(shell, force, silent, cancellationToken))
					.WithDescription("Install shell completion into the selected shell profile.");
				completion.Map(
					"uninstall",
					(string? shell, bool? silent, CancellationToken cancellationToken) =>
						app.HandleShellCompletionUninstallRouteAsync(shell, silent, cancellationToken))
					.WithDescription("Remove shell completion from the selected shell profile.");
				completion.Map(
					"status",
					() => app.HandleShellCompletionStatusRoute())
					.WithDescription("Show completion setup status and managed profile locations.");
				completion.Map(
					"detect-shell",
					() => app.HandleShellCompletionDetectShellRoute())
					.WithDescription("Detect the current shell using environment and parent process signals.");
			})
			.Hidden();
		}
	}
}
