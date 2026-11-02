namespace DocsMcpSample.Hosting;

internal sealed class HttpBoundary(RequestDelegate next, Settings settings, IHostApplicationLifetime host)
{
	private readonly HashSet<string> _origins = new(
		AllowedOrigins(settings),
		StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _hosts = new(
		AllowedOrigins(settings).Select(origin => HostString.FromUriComponent(new Uri(origin)).Host),
		StringComparer.OrdinalIgnoreCase);

	public async Task InvokeAsync(HttpContext context)
	{
		// Host headers are client-controlled; listener ownership comes from the accepted connection.
		var admin = context.Connection.LocalPort == settings.AdminPort;
		var origin = context.Request.Headers.Origin.ToString();
		if (admin && !_hosts.Contains(context.Request.Host.Host)) { context.Response.StatusCode = 403; return; }
		if (admin && origin.Length > 0 && !_origins.Contains(origin)) { context.Response.StatusCode = 403; return; }
		if (host.ApplicationStopping.IsCancellationRequested) { context.Response.StatusCode = 503; return; }
		if ((admin && context.Request.Path == "/mcp") || (!admin && context.Request.Path != "/mcp")) { context.Response.StatusCode = 404; return; }
		context.Response.Headers.XContentTypeOptions = "nosniff";
		await next(context);
	}

	private static string[] AllowedOrigins(Settings settings) =>
		[$"http://localhost:{settings.AdminPort}", $"http://127.0.0.1:{settings.AdminPort}", $"http://[::1]:{settings.AdminPort}", .. settings.AdminOrigin is { } origin ? new[] { origin } : []];
}
