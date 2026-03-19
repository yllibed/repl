using ModelContextProtocol.Server;
using static Repl.McpTests.McpTestFixture;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpTransportFactory
{
	[TestMethod]
	[Description("Custom transport factory is called and server operates through it.")]
	public async Task When_TransportFactoryProvided_Then_ServerUsesCustomTransport()
	{
		var factoryCalled = false;

		await using var fixture = await McpTestFixture.CreateAsync(
			app => app.Map("ping", () => "pong"),
			options =>
			{
				options.TransportFactory = (serverName, io) =>
				{
					factoryCalled = true;
					var ctx = (PipeIoContext)io;
					return new StreamServerTransport(ctx.InputStream, ctx.OutputStream, serverName);
				};
			}).ConfigureAwait(false);

		factoryCalled.Should().BeTrue();

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		tools.Should().ContainSingle(t => string.Equals(t.Name, "ping", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("When no factory is configured, default stdio transport is used (null factory).")]
	public void When_NoTransportFactory_Then_PropertyIsNull()
	{
		var options = new ReplMcpServerOptions();

		options.TransportFactory.Should().BeNull();
	}
}
