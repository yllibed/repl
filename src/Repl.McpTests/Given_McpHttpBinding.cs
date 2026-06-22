using Repl.Mcp.AspNetCore;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpHttpBinding
{
	[TestMethod]
	public void When_DefaultBindingCreated_Then_UsesLocalReplPort()
	{
		var binding = McpHttpBindingFactory.Create(
			host: null,
			ReplMcpHttpServerOptions.DefaultPort,
			path: null,
			allowRemote: false);

		binding.Host.Should().Be("127.0.0.1");
		binding.Port.Should().Be(7375);
		binding.Path.Should().Be("/mcp");
		binding.EndpointUrl.Should().Be("http://127.0.0.1:7375/mcp");
		binding.AllowsRemote.Should().BeFalse();
	}

	[TestMethod]
	public void When_RemoteBindingWithoutOptIn_Then_Fails()
	{
		var action = () => McpHttpBindingFactory.Create(
			"0.0.0.0",
			ReplMcpHttpServerOptions.DefaultPort,
			"/mcp",
			allowRemote: false);

		action.Should().Throw<InvalidOperationException>()
			.WithMessage("*--allow-remote*");
	}

	[TestMethod]
	public void When_RemoteBindingAllowed_Then_UsesRequestedHost()
	{
		var binding = McpHttpBindingFactory.Create(
			"0.0.0.0",
			4342,
			"mcp",
			allowRemote: true);

		binding.ListenUrl.Should().Be("http://0.0.0.0:4342");
		binding.EndpointUrl.Should().Be("http://0.0.0.0:4342/mcp");
		binding.AllowsRemote.Should().BeTrue();
	}

	[TestMethod]
	public void When_Ipv6LoopbackBindingCreated_Then_HostIsBracketedInUrl()
	{
		var binding = McpHttpBindingFactory.Create(
			"::1",
			7375,
			"/mcp",
			allowRemote: false);

		binding.ListenUrl.Should().Be("http://[::1]:7375");
		binding.EndpointUrl.Should().Be("http://[::1]:7375/mcp");
		binding.AllowsRemote.Should().BeFalse();
	}
}
