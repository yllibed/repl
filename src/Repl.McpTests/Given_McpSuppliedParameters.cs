using Repl.Mcp;
using Repl.Parameters;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpSuppliedParameters
{
	[TestMethod]
	[Description("Verifies a tool argument named like a parameter Repl supplies reaches the options-group property of that name, while the handler still receives the supplied progress reporter.")]
	public async Task When_AToolArgumentIsNamedLikeASuppliedParameter_Then_BothBind()
	{
		ProgressNamedOptions? bound = null;
		IProgress<double>? reporter = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("sync", (ProgressNamedOptions options, IProgress<double> progress) =>
			{
				bound = options;
				reporter = progress;
				return "ok";
			}));

		var result = await fixture.Client.CallToolAsync(
			"sync", new Dictionary<string, object?>(StringComparer.Ordinal) { ["progress"] = true }).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().NotBeNull();
		bound!.Progress.Should().BeTrue();
		reporter.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Verifies a parameter Repl supplies that has no value outside an MCP session, such as sampling, is left empty without consuming the next positional argument.")]
	public async Task When_ASuppliedServiceIsUnavailable_Then_TheNextPositionalStillBinds()
	{
		var app = ReplApp.Create();
		string? received = null;
		IMcpSampling? sampling = null;
		app.Map("greet", (IMcpSampling client, string name) =>
		{
			sampling = client;
			received = name;
			return name;
		});

		var code = await app.RunAsync(["greet", "foo", "--no-logo"]).ConfigureAwait(false);

		code.Should().Be(0);
		received.Should().Be("foo");
		sampling.Should().BeNull();
	}

	[ReplOptionsGroup]
	public sealed class ProgressNamedOptions
	{
		public bool Progress { get; set; }
	}
}
