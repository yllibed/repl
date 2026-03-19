using ModelContextProtocol.Protocol;
using Repl.Documentation;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpResourceParameters
{
	[TestMethod]
	[Description("Parameterized resource read passes URI template variables to the command handler.")]
	public async Task When_ParameterizedResourceRead_Then_ArgumentsArePassed()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("config {env}", (string env) => $"config-{env}")
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.ReadResourceAsync("repl://config/production").ConfigureAwait(false);

			var text = result.Contents.OfType<TextResourceContents>().First().Text;
			text.Should().Contain("config-production");
		}
	}

	[TestMethod]
	[Description("Parameterized resource appears in resource templates list with URI template.")]
	public async Task When_ParameterizedResource_Then_TemplateListIncludesVariables()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("config {env}", (string env) => $"config-{env}")
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var templates = await session.Client.ListResourceTemplatesAsync().ConfigureAwait(false);

			templates.Should().Contain(t => t.UriTemplate.Contains("{env}", StringComparison.Ordinal));
		}
	}

	[TestMethod]
	[Description("IsMatch correctly matches concrete URIs against templates.")]
	public void When_IsMatchCalledWithConcreteUri_Then_ReturnsTrue()
	{
		var resource = new ReplDocResource(
			Path: "config {env}", Description: "desc", Details: null, Arguments: [], Options: []);
		var sut = new ReplMcpServerResource(resource, resourceName: "config", uriTemplate: "repl://config/{env}", adapter: null!);

		sut.IsMatch("repl://config/production").Should().BeTrue();
		sut.IsMatch("repl://config/staging").Should().BeTrue();
		sut.IsMatch("repl://other/production").Should().BeFalse();
		sut.ProtocolResourceTemplate.UriTemplate.Should().Be("repl://config/{env}");
		sut.IsTemplated.Should().BeTrue();
	}

	[TestMethod]
	[Description("Parameterless resource read still works (regression).")]
	public async Task When_ParameterlessResourceRead_Then_ReturnsOutput()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("status", () => "all-ok")
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.ReadResourceAsync("repl://status").ConfigureAwait(false);

			var text = result.Contents.OfType<TextResourceContents>().First().Text;
			text.Should().Contain("all-ok");
		}
	}

	[TestMethod]
	[Description("Custom ResourceUriScheme is used in resource URIs.")]
	public async Task When_CustomScheme_Then_ResourceUriUsesScheme()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("status", () => "ok").ReadOnly().AsResource(),
			options => options.ResourceUriScheme = "myapp").ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.ReadResourceAsync("myapp://status").ConfigureAwait(false);

			var text = result.Contents.OfType<TextResourceContents>().First().Text;
			text.Should().Contain("ok");
		}
	}
}
