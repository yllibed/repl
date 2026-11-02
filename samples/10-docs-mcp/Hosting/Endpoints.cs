using System.Net.WebSockets;
using DocsMcpSample.Search;
using HostingRemoteSample;
using Repl;
using Repl.WebSocket;
using Repl.Remote;

namespace DocsMcpSample.Hosting;

internal static class Endpoints
{
	internal static void MapAdministration(this WebApplication app)
	{
		// Apply administration authorization to this group, including its WebSocket upgrade.
		var administration = app.MapGroup("");
		administration.MapGet("/admin/status", (IndexingWorker worker, Sessions sessions) => new { Index = worker.Status, Sessions = sessions.List() });
		administration.MapGet("/admin/compare", (string query, DocumentIndex index, IndexingWorker worker, CancellationToken ct) => { worker.RequireReady(); return index.Search(query, 5, ct); });
		administration.MapGet("/admin/fetch", (string id, DocumentIndex index, IndexingWorker worker) => { worker.RequireReady(); return index.Fetch(id, 20000); });
		administration.MapPost("/admin/index/{action}", async (string action, HttpContext context, ReplApp repl) =>
		{
			if (action is not ("rebuild" or "cancel")) { return Microsoft.AspNetCore.Http.Results.NotFound(); }
			await using var backend = new ReplAppRemoteBackend(repl, context.RequestServices);
			var result = await backend.ExecuteAsync(new() { Arguments = ["index", action] }, context.RequestAborted);
			return Microsoft.AspNetCore.Http.Results.Json(result, statusCode: result.ExitCode == 0 ? 200 : 400);
		});
		administration.Map("/ws/repl", RunTerminalAsync);
	}

	private static async Task RunTerminalAsync(HttpContext context, ReplApp repl, Sessions sessions, IHostApplicationLifetime host)
	{
		if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
		using var socket = await context.WebSockets.AcceptWebSocketAsync();
		await using var lifetime = new SessionLifetime(host.ApplicationStopping, context.RequestAborted);
		var id = sessions.Add("websocket");
		try
		{
			var query = context.Request.Query;
			var columns = int.TryParse(query["cols"], out var width) ? Math.Clamp(width, 20, 240) : 100;
			var rows = int.TryParse(query["rows"], out var height) ? Math.Clamp(height, 5, 100) : 30;
			await ReplWebSocketSession.RunWithServicesAsync(repl, socket, context.RequestServices, new ReplRunOptions
			{
				SessionScope = SessionScopeBehavior.CallerOwned,
				TerminalOverrides = new TerminalSessionOverrides { TransportName = "websocket", AnsiSupported = true, TerminalIdentity = "xterm-256color", WindowSize = (columns, rows) },
			}, cancellationToken: lifetime.Token);
		}
		catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		catch (WebSocketException) when (socket.State != WebSocketState.Open) { }
		finally
		{
			sessions.Remove(id);
			using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
			try
			{
				if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token); }
			}
			catch (OperationCanceledException) { socket.Abort(); }
			catch (WebSocketException) { socket.Abort(); }
		}
	}
}
