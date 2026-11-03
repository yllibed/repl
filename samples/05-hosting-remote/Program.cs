using HostingRemoteSample;

using Microsoft.Extensions.DependencyInjection;

using Repl;
using Repl.Remote;

// Client modes must exit before building the web host or starting another server instance.
if (args is ["--remote-console" or "--remote-exec", ..])
{
	return await RemoteClientMode.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Services.AddSignalR();

// Shared services — available to both ASP.NET endpoints and the REPL module.
builder.Services.AddSingleton<ISettingsService, InMemorySettingsService>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<SignalRSessions>();

// REPL application — resolved lazily so the module can pull from host DI.
builder.Services.AddRepl((sp, replApp) =>
{
	replApp.UseEmbeddedConsoleProfile();
	replApp.Options(options =>
	{
		options.Interaction.AdvancedProgressMode = AdvancedProgressMode.Auto;
		options.Interaction.ProgressTemplate = "{label}: {percent:0}%";
	});
	replApp.WithDescription("Remote REPL sample — settings, messaging, session tracking.");
	replApp.MapModule(new RemoteModule(
		sp.GetRequiredService<ISettingsService>(),
		sp.GetRequiredService<IMessageBus>(),
		sp.GetRequiredService<SessionTracker>(),
		sp.GetRequiredService<IHostApplicationLifetime>(),
		sp.GetRequiredService<ILogger<RemoteModule>>()));
});

await using var webApp = builder.Build();
webApp.Use(async (context, next) =>
{
	if (webApp.Lifetime.ApplicationStopping.IsCancellationRequested)
	{
		context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
		return;
	}
	await next(context);
});

webApp.UseWebSockets();
webApp.UseDefaultFiles();
webApp.UseStaticFiles();

webApp.MapWebSocketTerminals();
webApp.MapHub<ReplHub>("/hub/repl");

var repl = webApp.Services.GetRequiredService<ReplApp>();
var remoteFailed = LoggerMessage.Define(LogLevel.Warning, eventId: default, "Remote CLI failure");
var pipeName = webApp.Configuration["Repl:PipeName"] ?? "repl-hosting-sample";
var remote = new NamedPipeReplServer(pipeName,
	_ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(repl, webApp.Services)),
	error => remoteFailed(webApp.Logger, error));
return await HostingLifetime.RunAsync(webApp, remote);
