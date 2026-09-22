using System.Text.Json;
using System.Text.Json.Nodes;

namespace Repl.Tests;

/// <summary>
/// A command that returns <see cref="JsonNode"/> or <see cref="JsonElement"/> is returning JSON data, so
/// human output shows the JSON keys and values — not the CLR members (<c>Options</c>, <c>Parent</c>,
/// <c>Root</c>, <c>Count</c>) that reflection finds on those types.
/// </summary>
[TestClass]
public sealed class Given_HumanOutputJson
{
	private static readonly string[] ClrMembers = ["Options", "Parent", "Root", "Count", "ValueKind"];

	[TestMethod]
	[Description("The issue's own repro: a JsonObject result renders one line per JSON field, values as JSON literals, and none of JsonObject's CLR members.")]
	public async Task When_AJsonObjectIsRendered_Then_ItsFieldsAreShown()
	{
		var output = await RenderAsync(new JsonObject { ["id"] = 1, ["name"] = "Example", ["active"] = true });

		output.Should().MatchRegex(@"id\s*:\s*1");
		output.Should().MatchRegex(@"name\s*:\s*""Example""");
		output.Should().MatchRegex(@"active\s*:\s*true");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Paged JsonObject rows render as a table whose columns are the rows' JSON keys, in first-seen order, with a key missing from one row left empty rather than dropping the row.")]
	public async Task When_APageOfJsonObjectsIsRendered_Then_TheKeysBecomeColumns()
	{
		var page = new ReplPage<JsonObject>(
			[
				new JsonObject { ["id"] = 1, ["name"] = "first" },
				new JsonObject { ["id"] = 2, ["name"] = "second", ["extra"] = "only-here" },
			],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: 2, PageSize: 2));

		var output = await RenderAsync(page);

		var header = output.Split(Environment.NewLine)[0];
		header.Should().MatchRegex(@"id\s+name\s+extra");
		output.Should().Contain("\"first\"").And.Contain("\"second\"").And.Contain("\"only-here\"");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Nested objects and arrays stay visible as compact JSON, and an explicit JSON null reads as null rather than as an empty value.")]
	public async Task When_AJsonObjectHasNestedValuesAndNulls_Then_TheyArePreserved()
	{
		var output = await RenderAsync(new JsonObject
		{
			["owner"] = new JsonObject { ["login"] = "octo" },
			["tags"] = new JsonArray("a", "b"),
			["deleted"] = null,
		});

		output.Should().Contain("""{"login":"octo"}""");
		output.Should().Contain("""["a","b"]""");
		output.Should().MatchRegex(@"deleted\s*:\s*null");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("A JsonElement is JSON data too, and renders the same way as the equivalent JsonNode.")]
	public async Task When_AJsonElementIsRendered_Then_ItsFieldsAreShown()
	{
		using var document = JsonDocument.Parse("""{"id":7,"name":"from-element"}""");

		var output = await RenderAsync(document.RootElement);

		output.Should().MatchRegex(@"id\s*:\s*7");
		output.Should().MatchRegex(@"name\s*:\s*""from-element""");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("A JSON string is untrusted data: control characters in it, such as an ANSI escape, must reach the terminal escaped, not raw. Non-ASCII text stays readable.")]
	public async Task When_AJsonStringCarriesControlCharacters_Then_TheyAreEscaped()
	{
		var output = await RenderAsync(new JsonObject { ["note"] = "red\u001b[31malert", ["city"] = "Montréal" });

		output.Should().NotContain("\u001b", "a raw escape would let the payload drive the operator's terminal");
		output.Should().Contain("Montréal");
	}

	[TestMethod]
	[Description("Rendering reads the payload; it must not change it.")]
	public async Task When_AJsonObjectIsRendered_Then_ThePayloadIsUnchanged()
	{
		var payload = new JsonObject { ["id"] = 1, ["nested"] = new JsonObject { ["x"] = 2 } };
		var before = payload.ToJsonString();

		await RenderAsync(payload);

		payload.ToJsonString().Should().Be(before);
	}

	[TestMethod]
	[Description("A key missing from one row leaves that row's cell empty: it must not read null, and the other cells must not shift into it.")]
	public async Task When_ARowLacksAKey_Then_ItsCellIsEmpty()
	{
		var page = new ReplPage<JsonObject>(
			[
				new JsonObject { ["id"] = 1, ["name"] = "first" },
				new JsonObject { ["id"] = 2, ["name"] = "second", ["extra"] = "only-here" },
			],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: 2, PageSize: 2));

		var output = await RenderAsync(page);

		var firstRow = output.Split(Environment.NewLine).Single(line => line.Contains("\"first\"", StringComparison.Ordinal));
		firstRow.TrimEnd().Should().EndWith("\"first\"", "the extra column is empty for the row that does not have it");
	}

	[TestMethod]
	[Description("A top-level JSON array of scalars renders one JSON literal per line, and its JSON nulls read as null — also when every item is null, where there is no first value to recognize the array by.")]
	public async Task When_AJsonArrayOfScalarsIsRendered_Then_EachItemIsALiteral()
	{
		(await RenderAsync(new JsonArray(1, "two", null))).Split(Environment.NewLine)
			.Should().Equal("1", "\"two\"", "null");
		(await RenderAsync(new JsonArray(null, null))).Split(Environment.NewLine)
			.Should().Equal("null", "null");
	}

	[TestMethod]
	[Description("A page of JsonElement rows renders like a page of JsonObject rows: the issue names JsonElement explicitly.")]
	public async Task When_APageOfJsonElementsIsRendered_Then_TheKeysBecomeColumns()
	{
		using var document = JsonDocument.Parse("""[{"id":1,"name":"first"},{"id":2,"name":"second"}]""");
		var page = new ReplPage<JsonElement>(
			[.. document.RootElement.EnumerateArray()],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: 2, PageSize: 2));

		var output = await RenderAsync(page);

		output.Split(Environment.NewLine)[0].Should().MatchRegex(@"id\s+name");
		output.Should().Contain("\"second\"");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("A JsonNode held by an ordinary result object's property renders as a compact JSON literal, not as that node's CLR members.")]
	public async Task When_AnObjectHasAJsonProperty_Then_ItRendersAsALiteral()
	{
		var output = await RenderAsync(new Holder("h1", new JsonObject { ["x"] = 1 }));

		output.Should().MatchRegex(@"Payload\s*:\s*\{""x"":1\}");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("A JsonValue can wrap an arbitrary CLR object. Rendering it must not throw where --json succeeds: serializing through options that carry no type resolver does exactly that.")]
	public async Task When_AJsonValueWrapsAClrObject_Then_ItRenders()
	{
		var output = await RenderAsync(new JsonObject { ["owner"] = JsonValue.Create(new Owner("octo")) });

		output.Should().Contain("""{"Login":"octo"}""");
	}

	[TestMethod]
	[Description("JSON carried as an IReplResult's details renders as JSON data too, not as JsonNode's CLR members.")]
	public async Task When_AResultCarriesJsonDetails_Then_TheFieldsAreShown()
	{
		var output = await RenderAsync(Results.Success("done", new JsonObject { ["code"] = 42 }));

		output.Should().MatchRegex(@"code\s*:\s*42");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Bidirectional and other format characters (U+202E, U+2066 to U+2069, U+200B and others) are escaped too: left raw they reorder or hide what the terminal shows, so a value could display as something it is not.")]
	public async Task When_AJsonStringCarriesBidiControls_Then_TheyAreEscaped()
	{
		var output = await RenderAsync(new JsonObject { ["file\u202Egpj"] = "admin\u202Eexe.txt" });

		output.Should().NotContain("\u202E", "a raw override would reorder what the terminal displays");
		output.Should().Contain(@"\u202E");
	}

	private sealed record Holder(string Name, JsonObject Payload);

	private sealed record Owner(string Login);

	private static async Task<string> RenderAsync(object value)
	{
		var transformer = new HumanOutputTransformer(
			() => new HumanRenderSettings(
				Width: 120,
				UseAnsi: false,
				Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));
		return await transformer.TransformAsync(value, CancellationToken.None).ConfigureAwait(false);
	}

	private static void AssertNoClrMembers(string output)
	{
		foreach (var member in ClrMembers)
		{
			output.Should().NotContain(member, "JSON data must not render the CLR members of its container type");
		}
	}
}
