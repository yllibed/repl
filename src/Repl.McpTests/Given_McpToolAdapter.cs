using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Repl.Mcp;
using Repl.Documentation;
using Repl.Parameters;
using System.Text.Json;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpToolAdapter
{
	[TestMethod]
	public void When_RouteAndOptionValuesArePrepared_Then_TheirNativeShapesAreRetained()
	{
		var command = Command("contacts {id:int}", ["id"], ["format"]);
		var (request, transport, prefills) = Adapter().PrepareStructuredExecution(command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["id"] = JsonSerializer.SerializeToElement(-42),
				["format"] = JsonSerializer.SerializeToElement("--result:all"),
			});
		request.Command.Should().Be("contacts {id:int}");
		((JsonElement)request.Values["id"]!).GetInt32().Should().Be(-42);
		((JsonElement)request.Values["format"]!).GetString().Should().Be("--result:all");
		request.Arguments.Should().BeEmpty();
		transport.Should().BeEmpty();
		prefills.Should().BeEmpty();
	}

	[TestMethod]
	public void When_PagingAndAnswersArePrepared_Then_TheyStayOutsideCommandValues()
	{
		var command = Command("contacts", options: ["format"]) with
		{
			AcceptsPagingInput = true,
			Answers = [new ReplDocAnswer("confirm", "bool", Description: null)],
		};
		var (request, transport, prefills) = Adapter().PrepareStructuredExecution(command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["_replcursor"] = JsonSerializer.SerializeToElement("abc_DEF-123"),
				["_replpagesize"] = JsonSerializer.SerializeToElement(25),
				["answer.confirm"] = JsonSerializer.SerializeToElement(value: false),
				["format"] = JsonSerializer.SerializeToElement("json"),
			});
		transport.Should().Equal(ReplResultFlowOptionNames.Cursor, "abc_DEF-123", ReplResultFlowOptionNames.PageSize, "25");
		request.Values.Keys.Should().Equal("format");
		prefills.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("confirm", "false"));
	}

	[TestMethod]
	[DataRow("abc def", "*whitespace*")]
	[DataRow("--result:all", "*option*")]
	[DataRow("abc\u001b[2J", "*control*")]
	public void When_ResultCursorIsInvalid_Then_TransportValidationRejectsIt(string cursor, string message)
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("contacts") with { AcceptsPagingInput = true },
			new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement(cursor) });
		action.Should().Throw<InvalidOperationException>().WithMessage(message);
	}

	[TestMethod]
	public void When_ResultCursorIsTooLong_Then_TransportValidationRejectsIt()
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("contacts") with { AcceptsPagingInput = true },
			new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [McpResultFlowArgumentNames.Cursor] = JsonSerializer.SerializeToElement(new string('a', 513)) });
		action.Should().Throw<InvalidOperationException>().WithMessage("*512*");
	}

	[TestMethod]
	[DataRow("abc", "*numeric*")]
	[DataRow("-1", "*numeric*")]
	[DataRow("0", "*positive*")]
	[DataRow("9999999999", "*32-bit*")]
	[DataRow("11111111111", "*10 characters*")]
	public void When_ResultPageSizeIsInvalid_Then_TransportValidationRejectsIt(string size, string message)
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("contacts") with { AcceptsPagingInput = true },
			new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [McpResultFlowArgumentNames.PageSize] = JsonSerializer.SerializeToElement(size) });
		action.Should().Throw<InvalidOperationException>().WithMessage(message);
	}

	[TestMethod]
	[DataRow("secret")]
	[DataRow("answer.confirm")]
	[DataRow("_replCursor")]
	public void When_FieldIsAbsentFromTheSchema_Then_ItCannotEnterTheRequest(string field)
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("run", options: ["visible"]),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [field] = JsonSerializer.SerializeToElement("value") });
		action.Should().Throw<InvalidOperationException>().WithMessage("*not defined*schema*");
	}

	[TestMethod]
	public void When_CaseFallbackDuplicatesAField_Then_PreparationRejectsIt()
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("run", options: ["tenant"]),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["tenant"] = JsonSerializer.SerializeToElement("south"),
				["TENANT"] = JsonSerializer.SerializeToElement("north"),
			});
		action.Should().Throw<InvalidOperationException>().WithMessage("*TENANT*duplicates*tenant*");
	}

	[TestMethod]
	public void When_FieldCaseIsAmbiguous_Then_PreparationRejectsIt()
	{
		var action = () => Adapter().PrepareStructuredExecution(Command("run", options: ["tenant", "TENANT"]),
			new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["Tenant"] = JsonSerializer.SerializeToElement("value") });
		action.Should().Throw<InvalidOperationException>().WithMessage("*ambiguous*tenant*TENANT*");
	}

	[TestMethod]
	public void When_CaseDistinctFieldsAreMapped_Then_TheyKeepTheirDeclaredDestinations()
	{
		var app = CoreReplApp.Create().Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		app.Map("run", ([ReplOption(Name = "tenant")] string? lower, [ReplOption(Name = "TENANT")] string? upper) => $"{lower}:{upper}");
		var command = app.CreateDocumentationModel().Commands.Single();
		var (request, _, _) = Adapter(app).PrepareStructuredExecution(command,
			new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["tenant"] = JsonSerializer.SerializeToElement("south"),
				["TENANT"] = JsonSerializer.SerializeToElement("north"),
			});
		((JsonElement)request.Values["lower"]!).GetString().Should().Be("south");
		((JsonElement)request.Values["upper"]!).GetString().Should().Be("north");
	}

	[TestMethod]
	public async Task When_BoolValueLooksLikeAHiddenOption_Then_ItCannotBindThatOption()
	{
		string? observedHidden = null;
		await using var fixture = await McpTestFixture.CreateAsync(app => app.Map("deploy",
			([ReplOption] bool verbose, [ReplOption(Hidden = true, Aliases = ["-t"])] string? tenant) =>
			{
				observedHidden = tenant;
				Assert.Fail("Invalid boolean input must not dispatch.");
				return "unused";
			}));
		var result = await fixture.Client.CallToolAsync("deploy",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["verbose"] = "-t=secret" });
		result.IsError.Should().BeTrue();
		observedHidden.Should().BeNull();
	}

	[TestMethod]
	[Description("An MCP string value that names a response file remains literal during the programmatic invocation, so file contents cannot inject a hidden route option.")]
	public async Task When_StringToolValueNamesResponseFile_Then_ProgrammaticInvocationDoesNotExpandIt()
	{
		var responseFile = Path.Join(Path.GetTempPath(), $"repl-mcp-review-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, "ordinaryValue --hidden secret");
		try
		{
			string? observedVisible = null;
			string? observedHidden = null;
			var app = ReplApp.Create();
			app.Map(
					"deploy",
					([ReplOption] string? visible, [ReplOption] string? hidden) =>
					{
						observedVisible = visible;
						observedHidden = hidden;
						return "ok";
					})
				.WithOption("hidden", static option => option.Hidden());
			await using var services = new ServiceCollection().BuildServiceProvider();
			var adapter = new McpToolAdapter(
				app.Core, new ReplMcpServerOptions(), services, new McpRequestServerAccessor());
			adapter.RegisterRoute(
				"deploy",
				new ReplDocCommand(
					Path: "deploy",
					Description: null,
					Aliases: [],
					IsHidden: false,
					Arguments: [],
					Options: [new ReplDocOption("visible", "string", Required: false, Description: null, Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)]));

			var result = await adapter.InvokeAsync(
				"deploy",
				new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					["visible"] = JsonSerializer.SerializeToElement($"@{responseFile}"),
				},
				server: null,
				progressToken: null,
				CancellationToken.None);

			result.IsError.Should().NotBeTrue();
			observedHidden.Should().BeNull();
			observedVisible.Should().Be($"@{responseFile}");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	private static McpToolAdapter Adapter(CoreReplApp? app = null) =>
		new(app ?? CoreReplApp.Create(), new ReplMcpServerOptions(), McpTestFixture.EmptyServices, new McpRequestServerAccessor());

	private static ReplDocCommand Command(string path, string[]? arguments = null, string[]? options = null) =>
		new(path, Description: null, Aliases: [], IsHidden: false,
			Arguments: (arguments ?? []).Select(name => new ReplDocArgument(name, "string", Required: true, Description: null)).ToArray(),
			Options: (options ?? []).Select(name => new ReplDocOption(name, "string", Required: false, Description: null,
				Aliases: [], ReverseAliases: [], ValueAliases: [], EnumValues: [], DefaultValue: null)).ToArray());
}
