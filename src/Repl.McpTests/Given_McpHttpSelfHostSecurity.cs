using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Repl.Mcp.AspNetCore;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpHttpSelfHostSecurity
{
	[TestMethod]
	public async Task When_RequestHasLoopbackHostAndNoOrigin_Then_RequestContinues()
	{
		var called = false;
		var middleware = CreateMiddleware(_ =>
		{
			called = true;
			return Task.CompletedTask;
		});
		var context = CreateContext("127.0.0.1:7375");

		await middleware.InvokeAsync(context);

		called.Should().BeTrue();
		context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
	}

	[TestMethod]
	public async Task When_RequestHasUnexpectedHost_Then_RequestIsRejected()
	{
		var called = false;
		var middleware = CreateMiddleware(_ =>
		{
			called = true;
			return Task.CompletedTask;
		});
		var context = CreateContext("example.com");

		await middleware.InvokeAsync(context);

		called.Should().BeFalse();
		context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
	}

	[TestMethod]
	public async Task When_RequestHasUnexpectedBrowserOrigin_Then_RequestIsRejected()
	{
		var called = false;
		var middleware = CreateMiddleware(_ =>
		{
			called = true;
			return Task.CompletedTask;
		});
		var context = CreateContext("127.0.0.1:7375", "https://example.com");

		await middleware.InvokeAsync(context);

		called.Should().BeFalse();
		context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
	}

	[TestMethod]
	public async Task When_RequestHasAllowedBrowserOrigin_Then_RequestContinues()
	{
		var called = false;
		var options = new ReplMcpHttpSecurityOptions();
		options.AllowedOrigins.Add("https://trusted.example");
		var middleware = CreateMiddleware(_ =>
		{
			called = true;
			return Task.CompletedTask;
		}, options);
		var context = CreateContext("localhost:7375", "https://trusted.example");

		await middleware.InvokeAsync(context);

		called.Should().BeTrue();
		context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
	}

	[TestMethod]
	public void When_ServerOptionsAreCloned_Then_NestedHttpAndSecurityOptionsAreCopied()
	{
		var options = new ReplMcpHttpServerOptions
		{
			Host = "localhost",
			Port = 5555,
			Path = "/tools",
			AllowRemote = true,
			Quiet = true,
		};
		options.Http.IdleTimeout = TimeSpan.FromMinutes(2);
		options.Http.MaxIdleSessionCount = 3;
		options.Http.PerSessionExecutionContext = true;
		options.Security.AllowAnyHost = true;
		options.Security.AllowedOrigins.Add("https://trusted.example");

		var clone = options.Clone();

		clone.Host.Should().Be("localhost");
		clone.Port.Should().Be(5555);
		clone.Path.Should().Be("/tools");
		clone.AllowRemote.Should().BeTrue();
		clone.Quiet.Should().BeTrue();
		clone.Http.IdleTimeout.Should().Be(TimeSpan.FromMinutes(2));
		clone.Http.MaxIdleSessionCount.Should().Be(3);
		clone.Http.PerSessionExecutionContext.Should().BeTrue();
		clone.Security.AllowAnyHost.Should().BeTrue();
		clone.Security.AllowedOrigins.Should().ContainSingle("https://trusted.example");
		clone.Security.AllowedOrigins.Should().NotBeSameAs(options.Security.AllowedOrigins);
	}

	private static ReplMcpHttpSecurityMiddleware CreateMiddleware(
		RequestDelegate next,
		ReplMcpHttpSecurityOptions? options = null) =>
		new(next, NullLogger<ReplMcpHttpSecurityMiddleware>.Instance, options ?? new ReplMcpHttpSecurityOptions());

	private static DefaultHttpContext CreateContext(string host, string? origin = null)
	{
		var context = new DefaultHttpContext();
		context.Request.Host = HostString.FromUriComponent(host);
		if (origin is not null)
		{
			context.Request.Headers.Origin = origin;
		}

		return context;
	}
}
