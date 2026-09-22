using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Repl.SpectreTests;

/// <summary>
/// The Spectre human transformer renders <see cref="JsonNode"/> and <see cref="JsonElement"/> results as
/// JSON data, the same way the default human transformer does, rather than reflecting over their CLR
/// members.
/// </summary>
[TestClass]
public sealed partial class Given_SpectreHumanOutputJson
{
	private static readonly string[] ClrMembers = ["Options", "Parent", "Root", "Count", "ValueKind"];

	[TestMethod]
	[Description("A JsonObject result renders one row per JSON field, values as JSON literals, and none of JsonObject's CLR members.")]
	public async Task When_AJsonObjectIsRendered_Then_ItsFieldsAreShown()
	{
		var output = await RenderAsync(new JsonObject { ["id"] = 1, ["name"] = "Example", ["active"] = true }).ConfigureAwait(false);

		output.Should().MatchRegex(@"id:\s+1");
		output.Should().MatchRegex(@"name:\s+""Example""");
		output.Should().MatchRegex(@"active:\s+true");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Paged JsonObject rows render as a table whose columns are the rows' JSON keys, with a key missing from one row left empty.")]
	public async Task When_APageOfJsonObjectsIsRendered_Then_TheKeysBecomeColumns()
	{
		var page = new ReplPage<JsonObject>(
			[
				new JsonObject { ["id"] = 1, ["name"] = "first" },
				new JsonObject { ["id"] = 2, ["name"] = "second", ["extra"] = "only-here" },
			],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: 2, PageSize: 2));

		var output = await RenderAsync(page).ConfigureAwait(false);

		output.Should().MatchRegex(@"id\s+name\s+extra");
		output.Should().Contain("\"first\"").And.Contain("\"second\"").And.Contain("\"only-here\"");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Nested objects and arrays stay visible as compact JSON, and an explicit JSON null reads as null.")]
	public async Task When_AJsonObjectHasNestedValuesAndNulls_Then_TheyArePreserved()
	{
		var output = await RenderAsync(new JsonObject
		{
			["owner"] = new JsonObject { ["login"] = "octo" },
			["tags"] = new JsonArray("a", "b"),
			["deleted"] = null,
		}).ConfigureAwait(false);

		output.Should().Contain("""{"login":"octo"}""");
		output.Should().Contain("""["a","b"]""");
		output.Should().MatchRegex(@"deleted:\s+null");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("A JsonElement is JSON data too, and renders the same way as the equivalent JsonNode.")]
	public async Task When_AJsonElementIsRendered_Then_ItsFieldsAreShown()
	{
		using var document = JsonDocument.Parse("""{"id":7,"name":"from-element"}""");

		var output = await RenderAsync(document.RootElement).ConfigureAwait(false);

		output.Should().MatchRegex(@"id:\s+7");
		output.Should().MatchRegex(@"name:\s+""from-element""");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Control characters in a JSON string, such as an ANSI escape, must reach the terminal escaped, not raw.")]
	public async Task When_AJsonStringCarriesControlCharacters_Then_TheyAreEscaped()
	{
		var output = await RenderRawAsync(new JsonObject { ["note"] = "red\u001b[31malert" }).ConfigureAwait(false);

		output.Should().NotContain("\u001b[31m", "a raw escape would let the payload drive the operator's terminal");
		output.Should().Contain(@"\u001B[31m");
	}

	[TestMethod]
	[Description("Rendering reads the payload; it must not change it.")]
	public async Task When_AJsonObjectIsRendered_Then_ThePayloadIsUnchanged()
	{
		var payload = new JsonObject { ["id"] = 1, ["nested"] = new JsonObject { ["x"] = 2 } };
		var before = payload.ToJsonString();

		await RenderAsync(payload).ConfigureAwait(false);

		payload.ToJsonString().Should().Be(before);
	}

	[TestMethod]
	[Description("A JsonValue can wrap an arbitrary CLR object. Rendering it must not throw where --json succeeds.")]
	public async Task When_AJsonValueWrapsAClrObject_Then_ItRenders()
	{
		var output = await RenderAsync(new JsonObject { ["owner"] = JsonValue.Create(new Owner("octo")) }).ConfigureAwait(false);

		output.Should().Contain("""{"Login":"octo"}""");
	}

	[TestMethod]
	[Description("A JSON array whose items are all null still renders each as null: there is no first value to recognize it by, so it cannot go through the generic collection path.")]
	public async Task When_AJsonArrayOfNullsIsRendered_Then_EachItemReadsNull()
	{
		var output = await RenderAsync(new JsonArray(null, null)).ConfigureAwait(false);

		output.Split(Environment.NewLine).Should().Equal("null", "null");
	}

	[TestMethod]
	[Description("A JsonNode held by an ordinary result object's property renders as a compact JSON literal, not as that node's CLR members.")]
	public async Task When_AnObjectHasAJsonProperty_Then_ItRendersAsALiteral()
	{
		var output = await RenderAsync(new Holder("h1", new JsonObject { ["x"] = 1 })).ConfigureAwait(false);

		output.Should().Contain("""{"x":1}""");
		AssertNoClrMembers(output);
	}

	[TestMethod]
	[Description("Bidirectional format characters are escaped too: left raw they reorder what the terminal displays.")]
	public async Task When_AJsonStringCarriesBidiControls_Then_TheyAreEscaped()
	{
		var output = await RenderRawAsync(new JsonObject { ["file\u202Egpj"] = "admin\u202Eexe.txt" }).ConfigureAwait(false);

		output.Should().NotContain("\u202E", "a raw override would reorder what the terminal displays");
		output.Should().Contain(@"\u202E");
	}

	private sealed record Holder(string Name, JsonObject Payload);

	private sealed record Owner(string Login);

	// Spectre bolds labels with ANSI; the assertions are about the data, so that styling is stripped. The
	// control-character test reads the raw output instead, since what reaches the terminal is its point.
	private static async Task<string> RenderAsync(object value) =>
		AnsiStyling().Replace(await RenderRawAsync(value).ConfigureAwait(false), string.Empty);

	private static async Task<string> RenderRawAsync(object value) =>
		await new SpectreHumanOutputTransformer().TransformAsync(value, CancellationToken.None).ConfigureAwait(false);

	[GeneratedRegex(@"\u001b\[[0-9;]*m", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex AnsiStyling();

	private static void AssertNoClrMembers(string output)
	{
		foreach (var member in ClrMembers)
		{
			output.Should().NotContain(member, "JSON data must not render the CLR members of its container type");
		}
	}
}
