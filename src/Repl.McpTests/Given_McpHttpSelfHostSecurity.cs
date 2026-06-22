using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
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

	[TestMethod]
	public void When_ServerOptionsAreCreated_Then_UsesConservativeStatefulLimits()
	{
		var options = new ReplMcpHttpServerOptions();

		options.Http.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30));
		options.Http.MaxIdleSessionCount.Should().Be(100);
	}

	[TestMethod]
	public void When_RemoteBindingUsesDefaultSecurity_Then_AnyHostIsAllowedButOriginsStayRestricted()
	{
		var binding = McpHttpBindingFactory.Create("0.0.0.0", 7375, "/mcp", allowRemote: true);
		var security = new ReplMcpHttpSecurityOptions();

		ReplMcpHttpServer.ApplyBindingSecurityDefaults(binding, security);

		security.AllowAnyHost.Should().BeTrue();
		security.AllowAnyOrigin.Should().BeFalse();
	}

	[TestMethod]
	public void When_RemoteBindingUsesCustomHostList_Then_HostListIsPreserved()
	{
		var binding = McpHttpBindingFactory.Create("0.0.0.0", 7375, "/mcp", allowRemote: true);
		var security = new ReplMcpHttpSecurityOptions();
		security.AllowedHosts.Clear();
		security.AllowedHosts.Add("internal.example");

		ReplMcpHttpServer.ApplyBindingSecurityDefaults(binding, security);

		security.AllowAnyHost.Should().BeFalse();
		security.AllowedHosts.Should().ContainSingle("internal.example");
	}

	[TestMethod]
	public async Task When_HttpSessionCompletes_Then_SessionItemIsReleased()
	{
		var app = ReplApp.Create();
		app.Map("ping", () => "pong");
		var transport = new HttpServerTransportOptions();
		var runCalled = false;
#pragma warning disable MCPEXP002
		transport.RunSessionHandler = (_, _, _) =>
		{
			runCalled = true;
			return Task.CompletedTask;
		};
#pragma warning restore MCPEXP002
		ReplMcpHttpServiceCollectionExtensions.ConfigureTransport(transport, app, new ReplMcpHttpOptions());
		var context = CreateContext("127.0.0.1:7375");
		context.RequestServices = new ServiceCollection().BuildServiceProvider();

		await transport.ConfigureSessionOptions!(context, new McpServerOptions(), CancellationToken.None);
		context.Items.Should().NotBeEmpty();

#pragma warning disable MCPEXP002
		await transport.RunSessionHandler!(context, null!, CancellationToken.None);
#pragma warning restore MCPEXP002

		runCalled.Should().BeTrue();
		context.Items.Should().BeEmpty();
	}

	[TestMethod]
	public async Task When_ExternalSessionConfigurationFails_Then_SessionItemIsReleased()
	{
		var app = ReplApp.Create();
		app.Map("ping", () => "pong");
		var transport = new HttpServerTransportOptions
		{
			ConfigureSessionOptions = (_, _, _) => throw new InvalidOperationException("boom"),
		};
		ReplMcpHttpServiceCollectionExtensions.ConfigureTransport(transport, app, new ReplMcpHttpOptions());
		var context = CreateContext("127.0.0.1:7375");
		context.RequestServices = new ServiceCollection().BuildServiceProvider();

		var act = () => transport.ConfigureSessionOptions!(context, new McpServerOptions(), CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("boom");
		context.Items.Should().BeEmpty();
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
