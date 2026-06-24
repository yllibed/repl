using Repl.Mcp;

namespace Repl.Mcp.AspNetCore;

internal sealed class McpHttpModule : IReplModule
{
	private readonly ReplApp _app;
	private readonly ReplMcpHttpServerOptions _options;
	private readonly ReplMcpServerOptions _mcpOptions;

	public McpHttpModule(ReplApp app, ReplMcpHttpServerOptions options)
	{
		_app = app;
		_options = options;
		_mcpOptions = new ReplMcpServerOptions();
		options.Http.ConfigureServer?.Invoke(_mcpOptions);
	}

	public void Map(IReplMap map)
	{
		map.Context(_mcpOptions.ContextName, mcp =>
		{
			mcp.Map("httpserve",
				HandleHttpServeAsync)
				.WithDescription("Start MCP Streamable HTTP server for agent integration.")
				.WithAlias("http", "http-serve")
				.Hidden();
		})
		.Hidden();
	}

	private async Task<object> HandleHttpServeAsync(
		IReplIoContext io,
		string? host,
		int? port,
		string? path,
		bool allowRemote,
		int? idleTimeoutSeconds,
		int? maxIdleSessions,
		bool quiet,
		CancellationToken cancellationToken)
	{
		var runOptions = CreateRunOptions(
			host,
			port,
			path,
			allowRemote,
			idleTimeoutSeconds,
			maxIdleSessions,
			quiet);

		try
		{
			await ReplMcpHttpServer.RunAsync(
				_app,
				runOptions,
				io.Output,
				cancellationToken).ConfigureAwait(false);
			return Results.Exit(0);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return Results.Exit(0);
		}
		catch (InvalidOperationException ex)
		{
			ReplMcpHttpDiagnostics.StartupFailures.Add(1);
			await io.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
			return Results.Exit(1);
		}
	}

	private ReplMcpHttpServerOptions CreateRunOptions(
		string? host,
		int? port,
		string? path,
		bool allowRemote,
		int? idleTimeoutSeconds,
		int? maxIdleSessions,
		bool quiet)
	{
		var runOptions = _options.Clone();
		ApplyEndpointOptions(runOptions, host, port, path);

		runOptions.AllowRemote |= allowRemote;
		runOptions.Quiet |= quiet;

		if (idleTimeoutSeconds is { } idleSeconds)
		{
			runOptions.Http.IdleTimeout = TimeSpan.FromSeconds(idleSeconds);
		}

		if (maxIdleSessions is { } maxSessions)
		{
			runOptions.Http.MaxIdleSessionCount = maxSessions;
		}

		return runOptions;
	}

	private static void ApplyEndpointOptions(
		ReplMcpHttpServerOptions runOptions,
		string? host,
		int? port,
		string? path)
	{
		if (!string.IsNullOrWhiteSpace(host))
		{
			runOptions.Host = host;
		}

		if (port is { } portValue)
		{
			runOptions.Port = portValue;
		}

		if (!string.IsNullOrWhiteSpace(path))
		{
			runOptions.Path = path;
		}
	}
}
