using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
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
		var sut = new ReplMcpServerResource(
			resource,
			resourceName: "config",
			uriTemplate: "repl://config/{env}",
			adapter: null!,
			mimeType: "application/json");

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
			var content = result.Contents.OfType<TextResourceContents>().First();

			content.Text.Should().Contain("all-ok");
			content.MimeType.Should().Be("application/json");
		}
	}

	[TestMethod]
	[Description("Undeclared resource MIME type defaults to the forced MCP output converter MIME type.")]
	public async Task When_ResourceHasNoDeclaredMimeType_Then_ListAndReadUseForcedJsonMimeType()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("ops status", () => new
				{
					Service = "checkout",
					Healthy = true,
				})
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var resources = await session.Client.ListResourcesAsync().ConfigureAwait(false);
			resources.Should().ContainSingle(r => string.Equals(r.Uri, "repl://ops/status", StringComparison.Ordinal)).Which
				.MimeType.Should().Be("application/json");

			var result = await session.Client.ReadResourceAsync("repl://ops/status").ConfigureAwait(false);
			var content = result.Contents.OfType<TextResourceContents>().Single();

			content.MimeType.Should().Be("application/json");
			content.Text.Should().Contain("checkout");
		}
	}

	[TestMethod]
	[Description("Resource templates use the forced MCP output converter MIME type.")]
	public async Task When_TemplatedResource_Then_TemplateUsesForcedJsonMimeType()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("docs {name}", (string name) => $"# {name}")
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var templates = await session.Client.ListResourceTemplatesAsync().ConfigureAwait(false);

			templates.Should().ContainSingle(t => t.UriTemplate.Contains("{name}", StringComparison.Ordinal)).Which
				.MimeType.Should().Be("application/json");
		}
	}

	[TestMethod]
	[Description("Resource reads bypass paged tool text summaries and keep serialized JSON content.")]
	public async Task When_ResourceReadReturnsPageAndSummaryOnlyMode_Then_ReadStillReturnsJson()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("contacts", (IReplPagingContext paging) =>
				paging.Page(
					new[]
					{
						new { Id = 1, Name = "Alice" },
					},
					nextCursor: "page-2",
					totalCount: 2))
				.ReadOnly()
				.AsResource(),
			options => options.PagedResultTextMode = McpPagedResultTextMode.SummaryOnly).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.ReadResourceAsync("repl://contacts").ConfigureAwait(false);
			var content = result.Contents.OfType<TextResourceContents>().Single();

			content.MimeType.Should().Be("application/json");
			content.Text.Should().Contain("\"items\"");
			content.Text.Should().Contain("page-2");
			content.Text.Should().NotContain("Returned 1 item(s).");
		}
	}

	[TestMethod]
	[Description("Resource reads discard low-level handler output and diagnostics so application/json only labels the JSON return payload.")]
	public async Task When_ResourceHandlerWritesSideChannelOutput_Then_ReadReturnsOnlyJsonPayload()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("mixed", (IReplIoContext io) =>
			{
				io.Output.WriteLine("side-channel text");
				io.Error.WriteLine("diagnostic text");
				return new { Value = 42 };
			})
				.ReadOnly()
				.AsResource(),
			configureServices: _ => { }).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var result = await session.Client.ReadResourceAsync("repl://mixed").ConfigureAwait(false);
			var content = result.Contents.OfType<TextResourceContents>().Single();

			content.MimeType.Should().Be("application/json");
			content.Text.Should().NotContain("side-channel text");
			content.Text.Should().NotContain("diagnostic text");
			using var json = JsonDocument.Parse(content.Text);
			json.RootElement.EnumerateObject().Single().Value.GetInt32().Should().Be(42);
		}
	}

	[TestMethod]
	[Description("Void resource handlers are serialized by the forced JSON converter as JSON null, not tool fallback text.")]
	public async Task When_ResourceHandlerReturnsVoid_Then_ReadUsesSerializedJsonNull()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("noop", () => { })
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var resources = await session.Client.ListResourcesAsync().ConfigureAwait(false);
			resources.Should().ContainSingle(r => string.Equals(r.Uri, "repl://noop", StringComparison.Ordinal)).Which
				.MimeType.Should().Be("application/json");

			var result = await session.Client.ReadResourceAsync("repl://noop").ConfigureAwait(false);
			var content = result.Contents.OfType<TextResourceContents>().Single();

			content.MimeType.Should().Be("application/json");
			content.Text.Should().Be("null");
		}
	}

	[TestMethod]
	[Description("Exit results without payload keep the advertised resource MIME by returning a JSON null payload.")]
	public async Task When_ResourceHandlerReturnsSuccessfulExitWithoutPayload_Then_ReadUsesSerializedJsonNull()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("silent", () => Results.Exit(0))
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			var resources = await session.Client.ListResourcesAsync().ConfigureAwait(false);
			resources.Should().ContainSingle(r => string.Equals(r.Uri, "repl://silent", StringComparison.Ordinal)).Which
				.MimeType.Should().Be("application/json");

			var result = await session.Client.ReadResourceAsync("repl://silent").ConfigureAwait(false);
			var content = result.Contents.OfType<TextResourceContents>().Single();

			content.MimeType.Should().Be("application/json");
			content.Text.Should().Be("null");
		}
	}

	[TestMethod]
	[Description("Failed resource commands surface as MCP exceptions instead of typed resource contents.")]
	public async Task When_ResourceCommandFails_Then_ReadThrowsMcpException()
	{
		var session = await McpTestFixture.CreateAsync(
			app => app.Map("boom", () => Results.Error("boom", "nope"))
				.ReadOnly()
				.AsResource()).ConfigureAwait(false);

		await using (session.ConfigureAwait(false))
		{
			Func<Task> act = async () => await session.Client.ReadResourceAsync("repl://boom").ConfigureAwait(false);

			var exception = (await act.Should().ThrowAsync<McpException>().ConfigureAwait(false)).Which;
			exception.Message.Should().Contain("nope");
		}
	}

	[TestMethod]
	[Description("The resource adapter reports unknown routes as text/plain errors.")]
	public async Task When_ResourceRouteIsUnknown_Then_AdapterReturnsTextError()
	{
		await using var services = new ServiceCollection().BuildServiceProvider();
		var adapter = new McpToolAdapter(ReplApp.Create().Core, new ReplMcpServerOptions(), services);

		var result = await adapter.InvokeResourceAsync(
			"missing",
			new Dictionary<string, JsonElement>(StringComparer.Ordinal),
			server: null,
			progressToken: null,
			ct: CancellationToken.None).ConfigureAwait(false);

		result.IsError.Should().BeTrue();
		result.MimeType.Should().Be("text/plain");
		result.Text.Should().Be("Unknown resource: missing");
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
