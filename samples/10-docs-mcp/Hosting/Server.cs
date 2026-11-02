using System.Net;
using DocsMcpSample.Search;
using ModelContextProtocol.AspNetCore;
using Repl;
using Repl.Mcp;
using Repl.Remote;
using Repl.Spectre;

namespace DocsMcpSample.Hosting;

internal static class Server
{
	internal static async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
	{
		settings.Validate();
		await Assets.VerifyAsync(settings.Assets, cancellationToken);

		await using var app = Create(settings);
		Log.InsecureAdministration(app.Logger, settings.AdminPort);
		if (!IPAddress.IsLoopback(settings.AdminAddress)) { Log.RemoteAdministration(app.Logger, settings.AdminAddress.ToString()); }

		var repl = app.Services.GetRequiredService<ReplApp>();
		var sessions = app.Services.GetRequiredService<Sessions>();
		var remote = new NamedPipeReplServer(settings.Pipe,
			_ => ValueTask.FromResult(sessions.Track(new ReplAppRemoteBackend(repl, app.Services))),
			error => Log.PipeFailure(app.Logger, error));

		using var stopping = cancellationToken.Register(app.Lifetime.StopApplication);
		return await ServerLifetime.RunAsync(app, remote);
	}

	internal static WebApplication Create(Settings settings, Action<IServiceCollection>? configureServices = null)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
		builder.Logging.ClearProviders().AddSimpleConsole();
		builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning).AddFilter("ModelContextProtocol", LogLevel.Warning);
		builder.WebHost.ConfigureKestrel(server =>
		{
			server.Listen(settings.McpAddress, settings.McpPort);
			server.Listen(settings.AdminAddress, settings.AdminPort);
		});
		builder.Services.AddSingleton(settings);
		builder.Services.AddProblemDetails();
		builder.Services.AddExceptionHandler<AdministrationErrors>();
		builder.Services.AddSingleton(TimeProvider.System);
		builder.Services.AddSingleton<ITextEmbedding, E5Embedding>();
		builder.Services.AddSingleton<MarkdownCorpus>();
		builder.Services.AddSingleton<DocumentIndex>();
		builder.Services.AddSingleton<IndexingWorker>();
		builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingWorker>());
		builder.Services.AddSingleton<Sessions>();
		builder.Services.AddSpectreConsole();
		builder.Services.AddRepl((services, repl) =>
		{
			repl.UseEmbeddedConsoleProfile().UseSpectreConsole(spectre =>
			{
				spectre.AddView<IndexStatus>(ConsoleViews.Status);
				spectre.AddView<SearchComparison>(ConsoleViews.Comparison);
				spectre.AddView<FetchedDocument>(ConsoleViews.Document);
			});
			repl.WithDescription("Documentation index — search, inspect and administer one live process.");
			repl.Options(options =>
			{
				options.AmbientCommands.ExitCommandEnabled = true;
			});
			Commands.Map(repl);
		});
		builder.Services.AddMcpServer().WithHttpTransport(http =>
		{
			http.SessionMode = HttpServerSessionMode.Stateless;
			http.ConfigureSessionOptions = (context, target, ct) =>
			{
				ct.ThrowIfCancellationRequested();
				context.RequestServices.GetRequiredService<ReplApp>().ConfigureMcpServerOptions(target, options =>
				{
					options.ServerName = "Documentation index";
					options.AutoPromoteReadOnlyToResources = false;
					options.CommandFilter = command => command.Path is "docs search" or "docs fetch";
				}, context.RequestServices);
				return Task.CompletedTask;
			};
		});
		configureServices?.Invoke(builder.Services);
		var app = builder.Build();
		app.UseMiddleware<HttpBoundary>();
		// AdministrationErrors owns diagnostics for handled failures (.NET 10 suppresses them by default).
		app.UseExceptionHandler();
		app.UseWebSockets();
		// Production authentication/authorization must also protect static files with an applicable policy.
		app.UseDefaultFiles();
		app.UseStaticFiles();
		app.UseStaticFiles(new StaticFileOptions { RequestPath = "/vendor", FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(settings.Assets, "web")) });
		// Attach the public audience's authorization and rate-limit policies here; keep the graph allowlist above.
		app.MapMcp("/mcp");
		app.MapAdministration();

		return app;
	}
}
