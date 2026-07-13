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
				async (
					string? shell,
					string? line,
					string? cursor,
					IReplIoContext io,
					IServiceProvider services,
					CancellationToken cancellationToken) =>
				{
					// The execution's service provider (run/session DI) flows to the value
					// providers — the core fallback provider would hide externally-registered
					// services from CompletionContext.Services.
					var result = await runtime.HandleBridgeRouteAsync(shell, line, cursor, services, cancellationToken)
						.ConfigureAwait(false);
					if (result is not string payload)
					{
						// Error/validation results are rendered by the framework (stderr in passthrough mode).
						return result;
					}

					if (!string.IsNullOrEmpty(payload))
					{
						await io.Output.WriteLineAsync(payload).ConfigureAwait(false);
					}

					return Results.Exit(0);
				})
				.WithDescription("Internal completion bridge used by shell integrations.")
				.AsProtocolPassthrough()
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
