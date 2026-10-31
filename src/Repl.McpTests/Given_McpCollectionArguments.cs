using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;
using Repl.Parameters;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpCollectionArguments
{
	private static readonly string[] Layers = ["raw", "indexed"];
	private static readonly int[] Counts = [-2, 0, 3];
	private static readonly bool[] Flags = [true, false, true];
	private static readonly double[] Weights = [1.25, -2.5];
	private static readonly string[] Names = ["first", "second"];
	[TestMethod]
	[Description("A collection option's default is not advertised in the tool schema: the schema's default is text, which an agent sending it back would bind as a single element.")]
	public async Task When_ACollectionOptionHasADefault_Then_TheSchemaAdvertisesNone()
	{
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("search", (SearchOptions options) => "ok"));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var layer = tools.Single(tool => string.Equals(tool.Name, "search", StringComparison.Ordinal)).JsonSchema
			.GetProperty("properties").GetProperty("layer");

		layer.GetProperty("type").GetString().Should().Be("array");
		layer.TryGetProperty("default", out _).Should().BeFalse();
	}

	[TestMethod]
	[DataRow("raw")]
	[DataRow("raw", "indexed")]
	[DataRow("")]
	[DataRow("-f", "--erase=true", "--", "a=b", "with spaces", "a,b", "quote\"line\n")]
	[Description("Issue #112: MCP arrays bind each element, not one raw JSON string, to options-group collections.")]
	public async Task When_AnArrayIsPassedToAnOptionsGroup_Then_EachElementIsBound(params string[] values)
	{
		SearchOptions? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("search", (SearchOptions options) => { bound = options; return "ok"; }));

		var tools = await fixture.Client.ListToolsAsync().ConfigureAwait(false);
		var schema = tools.Single(tool => string.Equals(tool.Name, "search", StringComparison.Ordinal)).JsonSchema;
		schema.GetProperty("properties").GetProperty("layer").GetProperty("type").GetString().Should().Be("array");
		var result = await fixture.Client.CallToolAsync(
			"search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["layer"] = values }).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().NotBeNull();
		bound!.Layer.Should().Equal(values);
		bound.Erase.Should().BeFalse("array elements must not be reinterpreted as hidden option tokens");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Empty arrays contribute no option occurrences and preserve omitted-option defaults.")]
	public async Task When_AnArrayIsEmptyOrOmitted_Then_TheOptionDefaultIsPreserved(bool supplyEmptyArray)
	{
		SearchOptions? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("search", (SearchOptions options) => { bound = options; return "ok"; }));
		var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
		if (supplyEmptyArray)
		{
			arguments["layer"] = Array.Empty<string>();
		}

		var result = await fixture.Client.CallToolAsync("search", arguments).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().NotBeNull();
		bound!.Layer.Should().Equal("default");
	}

	[TestMethod]
	[Description("The scalar workaround for MCP collection arguments remains supported.")]
	public async Task When_ACollectionReceivesAScalar_Then_OneValueIsBound()
	{
		string[]? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("search", (SearchOptions options) => { bound = options.Layer; return "ok"; }));

		var result = await fixture.Client.CallToolAsync(
			"search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["layer"] = "raw" }).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().Equal("raw");
	}

	[TestMethod]
	[Description("Schema collection shapes bind scalar elements through the existing typed collection binder.")]
	public async Task When_TypedCollectionsAreSupplied_Then_TheirElementsAreConverted()
	{
		TypedOptions? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("typed", (TypedOptions options) => { bound = options; return "ok"; }));
		var id = Guid.Empty;
		var result = await fixture.Client.CallToolAsync("typed", new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["count"] = Counts,
			["enabled"] = Flags,
			["weight"] = Weights,
			["id"] = new[] { id },
			["name"] = Names,
		}).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue("{0}", System.Text.Json.JsonSerializer.Serialize(result));
		bound.Should().NotBeNull();
		bound!.Count.Should().Equal(-2, 0, 3);
		bound.Enabled.Should().Equal(true, false, true);
		bound.Weight.Should().Equal(1.25, -2.5);
		bound.Id.Should().Equal(id);
		bound.Name.Should().Equal("first", "second");
	}

	[TestMethod]
	[DataRow("layer")]
	[DataRow("LaYeR")]
	[Description("Direct collections preserve argument resolution and the configured short option alias.")]
	public async Task When_ADirectCollectionHasAnAlias_Then_EachValueUsesTheResolvedOption(string key)
	{
		string[]? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("search", ([ReplOption(Name = "layer", Aliases = ["-l"])] string[] layer) =>
			{
				bound = layer;
				return "ok";
			}));
		var result = await fixture.Client.CallToolAsync(
			"search", new Dictionary<string, object?>(StringComparer.Ordinal) { [key] = Layers }).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().Equal("raw", "indexed");
	}

	[TestMethod]
	[Description("Only schema-declared collection options are expanded; scalar strings retain raw JSON behavior.")]
	public async Task When_AScalarOptionReceivesAnArray_Then_ItIsNotExpanded()
	{
		string? bound = null;
		await using var fixture = await McpTestFixture.CreateAsync(app =>
			app.Map("echo", (string value) => { bound = value; return "ok"; }));
		var result = await fixture.Client.CallToolAsync(
			"echo", new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = Layers }).ConfigureAwait(false);

		result.IsError.Should().NotBeTrue();
		bound.Should().Be("[\"raw\",\"indexed\"]");
	}

	[TestMethod]
	[DataRow("null", false)]
	[DataRow("{}", false)]
	[DataRow("[]", false)]
	[DataRow("null", true)]
	[DataRow("{}", true)]
	[DataRow("[]", true)]
	[Description("Both tool dispatch paths expose actionable collection validation without invoking the handler.")]
	public async Task When_AnArrayContainsANonScalar_Then_TheClientReceivesGuidance(string itemJson, bool usePrebuiltTools)
	{
		var invoked = false;
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("search", (SearchOptions options) => { invoked = true; return "ok"; });
		await using var session = await StartSessionAsync(app, usePrebuiltTools).ConfigureAwait(false);
		using var document = System.Text.Json.JsonDocument.Parse($"[\"raw\",{itemJson}]");
		var result = await session.Client.CallToolAsync(
			"search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["layer"] = document.RootElement }).ConfigureAwait(false);

		result.IsError.Should().BeTrue();
		invoked.Should().BeFalse();
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		text.Should().Contain("'layer'").And.Contain("non-null scalar values");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Exposing deliberate collection refusals does not expose unexpected application exception details.")]
	public async Task When_TheHandlerThrows_Then_PrivateDetailsRemainWithheld(bool usePrebuiltTools)
	{
		var invoked = false;
		var app = ReplApp.Create();
		app.UseMcpServer();
		app.Map("search", string (SearchOptions options) =>
		{
			invoked = true;
			throw new InvalidOperationException("private application detail");
		});
		await using var session = await StartSessionAsync(app, usePrebuiltTools).ConfigureAwait(false);
		var result = await session.Client.CallToolAsync(
			"search", new Dictionary<string, object?>(StringComparer.Ordinal) { ["layer"] = Layers }).ConfigureAwait(false);

		result.IsError.Should().BeTrue();
		invoked.Should().BeTrue();
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
		text.Should().NotContain("private application detail");
	}

	private static Task<McpPipeSession> StartSessionAsync(ReplApp app, bool usePrebuiltTools)
	{
		if (!usePrebuiltTools)
		{
			var handler = new McpServerHandler(app.Core,
				new ReplMcpServerOptions { TransportFactory = McpTestFixture.PipeTransportFactory }, app.Services);
			return McpPipeSession.StartAsync(handler.RunAsync, clientOptions: null, CancellationToken.None);
		}

		var options = app.BuildMcpServerOptions();
		return McpPipeSession.StartAsync(async (io, token) =>
		{
			var transport = new StreamServerTransport(io.InputStream, io.OutputStream, "collection-options-server");
			var server = McpServer.Create(transport, options);
			try
			{
				await server.RunAsync(token).ConfigureAwait(false);
			}
			finally
			{
				await server.DisposeAsync().ConfigureAwait(false);
				await transport.DisposeAsync().ConfigureAwait(false);
			}
		}, clientOptions: null, CancellationToken.None);
	}

	[ReplOptionsGroup]
	private sealed class SearchOptions
	{
		public string[]? Layer { get; set; } = ["default"];

		[ReplOption(Hidden = true)]
		public bool Erase { get; set; }
	}

	[ReplOptionsGroup]
	private sealed class TypedOptions
	{
		public List<int>? Count { get; set; }
		public bool[]? Enabled { get; set; }
		public IReadOnlyList<double>? Weight { get; set; }
		public IEnumerable<Guid>? Id { get; set; }
		public List<string>? Name { get; set; }
	}
}
