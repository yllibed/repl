using System.Net;
using DocsMcpSample;
using DocsMcpSample.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_HttpBoundary
{
	[TestMethod]
	[DataRow("/")]
	[DataRow("/app.js")]
	[DataRow("/admin/status")]
	[DataRow("/ws/repl")]
	[Description("An untrusted Host cannot reach administration or its static files by omitting Origin.")]
	public async Task When_AnUnknownHostOmitsOrigin_Then_AdministrationIsRejected(string path)
	{
		var context = Request("untrusted.example:8081", path);

		await Boundary().InvokeAsync(context).ConfigureAwait(false);

		context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
	}

	[TestMethod]
	[DataRow("localhost:8081")]
	[DataRow("LOCALHOST:8081")]
	[DataRow("127.0.0.1:8081")]
	[DataRow("[::1]:8081")]
	[DataRow("admin.example.com")]
	[Description("Native loopback clients and an explicitly configured proxy hostname remain usable without Origin.")]
	public async Task When_TheHostIsTrusted_Then_NativeRequestsAreAllowed(string host)
	{
		var context = Request(host, "/admin/status");

		await Boundary().InvokeAsync(context).ConfigureAwait(false);

		context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
	}

	[TestMethod]
	[Description("A trusted Host cannot bypass foreign-Origin validation, and cannot turn the MCP listener into administration.")]
	public async Task When_TheHostIsTrusted_Then_OriginAndListenerChecksStillApply()
	{
		var foreign = Request("localhost:8081", "/admin/status");
		foreign.Request.Headers.Origin = "https://untrusted.example";
		var publicRequest = Request("localhost:8081", "/admin/status");
		publicRequest.Connection.LocalPort = 8080;
		var publicMcp = Request("public.example", "/mcp");
		publicMcp.Connection.LocalPort = 8080;

		var boundary = Boundary();
		await boundary.InvokeAsync(foreign).ConfigureAwait(false);
		await boundary.InvokeAsync(publicRequest).ConfigureAwait(false);
		await boundary.InvokeAsync(publicMcp).ConfigureAwait(false);

		foreign.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
		publicRequest.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
		publicMcp.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
	}

	private static DefaultHttpContext Request(string host, string path)
	{
		var context = new DefaultHttpContext();
		context.Connection.LocalPort = 8081;
		context.Request.Host = HostString.FromUriComponent(host);
		context.Request.Path = path;
		return context;
	}

	private static HttpBoundary Boundary() => new(context =>
	{
		context.Response.StatusCode = StatusCodes.Status204NoContent;
		return Task.CompletedTask;
	}, new Settings("documents", "index", "assets", "pipe", IPAddress.Loopback, 8080, 8081)
	{
		AdminOrigin = "https://admin.example.com",
	}, new Lifetime());

	private sealed class Lifetime : IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => CancellationToken.None;
		public CancellationToken ApplicationStopped => CancellationToken.None;
		public void StopApplication() => throw new NotSupportedException();
	}
}
