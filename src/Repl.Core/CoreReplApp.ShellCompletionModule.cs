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

			map.Map(ShellCompletionSetupCommandName, () => app.HandleShellCompletionSetupRoute())
				.Hidden();
			map.Context(ShellCompletionSetupCommandName, completion =>
			{
				completion.Map(
					ShellCompletionProtocolSubcommandName,
					(string? shell, string? line, string? cursor) => app.HandleShellCompletionBridgeRoute(shell, line, cursor))
					.Hidden();
				completion.Map(
					"install",
					(string? shell, bool? force, CancellationToken cancellationToken) =>
						app.HandleShellCompletionInstallRouteAsync(shell, force, cancellationToken))
					.Hidden();
				completion.Map(
					"uninstall",
					(string? shell, CancellationToken cancellationToken) =>
						app.HandleShellCompletionUninstallRouteAsync(shell, cancellationToken))
					.Hidden();
				completion.Map(
					"status",
					() => app.HandleShellCompletionStatusRoute())
					.Hidden();
				completion.Map(
					"detect-shell",
					() => app.HandleShellCompletionDetectShellRoute())
					.Hidden();
				completion.Map(
					"{subCommand}",
					(string subCommand) => app.HandleShellCompletionUnknownSubcommandRoute(subCommand))
					.Hidden();
			});
		}
	}
}
