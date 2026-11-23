using HostingRemoteSample;
using Repl;
using Repl.Hosting.TestHost;
using Repl.Remote;

try
{
	if (args is not ["cooperative" or "async-cleanup" or "blocking-callback" or "slow-console-log" or "blocked-stderr"])
	{
		return 2;
	}
	// Installed before the console logger captures Console.Out, so the logged failure lags past its 1.5 s flush budget.
	if (args[0] is "slow-console-log") { Console.SetOut(new FatalLineDelayingWriter(Console.Out, TimeSpan.FromSeconds(5))); }
	if (args[0] is "blocked-stderr") { Console.SetError(new FatalLineDelayingWriter(Console.Error, Timeout.InfiniteTimeSpan)); }
	using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
	var builder = WebApplication.CreateBuilder();
	// Child-process assertions observe console logs; Windows Event Log requires unrelated machine permissions.
	builder.Logging.ClearProviders().AddSimpleConsole();
	builder.WebHost.UseUrls("http://127.0.0.1:0");
	builder.Services.AddSingleton<SessionTracker>();
	builder.Services.AddSingleton<SignalRSessions>();
	var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	builder.Services.AddRepl((_, app) =>
	{
		app.UseEmbeddedConsoleProfile();
		app.Map("wait", async (CancellationToken ct) =>
		{
			if (args[0] is "blocking-callback")
			{
				// Never unregistered: cancellation runs callbacks in reverse registration order, so the Task.Delay
				// callback below runs first, and the handler can return before cancellation reaches this one.
				// Disposing the registration on return would then skip the hostile callback, and the host would
				// shut down cleanly.
				ct.Register(static () =>
				{
					Console.WriteLine("blocking-callback-entered");
#pragma warning disable MA0045 // Deliberately hostile command callback, isolated in a child process.
					Thread.Sleep(Timeout.Infinite);
#pragma warning restore MA0045
				});
			}
			entered.SetResult();
			try { await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false); }
			finally
			{
				Console.WriteLine("command-cleaning");
				if (args[0] is "async-cleanup" or "slow-console-log" or "blocked-stderr")
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
				}
				Console.WriteLine("command-cleaned");
			}
		});
	});
	var host = builder.Build();
	await using var hostOwner = host.ConfigureAwait(false);
	var app = host.Services.GetRequiredService<ReplApp>();
	var listener = new NamedPipeReplServer(Guid.NewGuid().ToString("N")[..20],
		_ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app, host.Services)));
	var run = HostingLifetime.RunAsync(host, listener);
	var sessions = host.Services.GetRequiredService<SignalRSessions>();
	var terminal = new StreamedReplHost(TextWriter.Null);
	await using var terminalOwner = terminal.ConfigureAwait(false);
	sessions.Start("test", terminal, "test", new ReplRunOptions
	{
		TerminalOverrides = new TerminalSessionOverrides { AnsiSupported = false, WindowSize = (80, 24) },
	}, CancellationToken.None);
	terminal.EnqueueInput("wait\n");
	await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
	Console.WriteLine("shutdown-test-ready");
	await Console.In.ReadLineAsync(deadline.Token).ConfigureAwait(false);
	await Task.Run(host.Lifetime.StopApplication, CancellationToken.None).ConfigureAwait(false);
#pragma warning disable VSTHRD003 // The hosting coordinator runs independently of the shutdown trigger.
	return await run.ConfigureAwait(false);
#pragma warning restore VSTHRD003
}
#pragma warning disable CA1031 // Report test fixture failures without an unhandled-exception dialog.
catch (Exception error)
{
	await Console.Error.WriteLineAsync(error.ToString()).ConfigureAwait(false);
	return 2;
}
#pragma warning restore CA1031
