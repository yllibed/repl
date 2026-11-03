using DocsMcpSample.Hosting;
using DocsMcpSample.Search;
using Repl;
using Repl.Parameters;
using Results = Repl.Results;

namespace DocsMcpSample;

internal static class Commands
{
	internal static void Map(ReplApp repl)
	{
		repl.MapModule(new DocumentsModule()).MapModule(new IndexModule());
		repl.Map("sessions", ([FromServices] Sessions sessions) => sessions.List()).WithDescription("List browser sessions and local CLI connections.").ReadOnly();
		repl.Map("health", ([FromServices] IndexingWorker worker, [FromServices] IHostApplicationLifetime lifetime, [ReplOption] bool ready = false) =>
		{
			var alive = lifetime.ApplicationStarted.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested;
			return Results.Exit(alive && (!ready || worker.Status.Ready) ? 0 : 1, new { Alive = alive, Ready = worker.Status.Ready });
		}).WithDescription("Check process liveness; add --ready to require a usable index.").ReadOnly();
	}
}
