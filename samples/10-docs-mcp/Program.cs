using System.Net;
using DocsMcpSample;
using DocsMcpSample.Hosting;
using Repl;
using Repl.Remote;

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = lifetime.CancelAsync(); };
try
{
	var settings = Settings.FromEnvironment();
	if (args.Length > 0 && args is not ["serve"]) { return await ClientMode.RunAsync(args, settings, lifetime.Token); }
	settings.Validate();
	await Assets.VerifyAsync(settings.Assets, lifetime.Token);
	await using var app = Server.Create(settings);
	Log.InsecureAdministration(app.Logger, settings.AdminPort);
	if (!IPAddress.IsLoopback(settings.AdminAddress)) { Log.RemoteAdministration(app.Logger, settings.AdminAddress.ToString()); }
	var repl = app.Services.GetRequiredService<ReplApp>();
	var sessions = app.Services.GetRequiredService<Sessions>();
	var remote = new NamedPipeReplServer(settings.Pipe,
		_ => ValueTask.FromResult(sessions.Track(new ReplAppRemoteBackend(repl, app.Services))),
		error => Log.PipeFailure(app.Logger, error));
	using var stopping = lifetime.Token.Register(app.Lifetime.StopApplication);
	return await ServerLifetime.RunAsync(app, remote);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 130; }
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.Message);
	return 1;
}
