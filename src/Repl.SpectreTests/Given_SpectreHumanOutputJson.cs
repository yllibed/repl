using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Repl.Rendering;

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

	[TestMethod]
	[Description("A continuation page carries its own JSON header: its rows' keys can come in another order, or be other keys, than the first page's, whose headings would then mislabel its values.")]
	public async Task When_AContinuationPageHasOtherKeys_Then_ItCarriesItsOwnHeader()
	{
		var page = new ReplPage<JsonObject>(
			[new JsonObject { ["name"] = "b", ["id"] = 2 }],
			new ReplPageInfo(Cursor: "1", NextCursor: null, TotalCount: null, PageSize: 1));

		var raw = await new SpectreHumanOutputTransformer()
			.TransformPageAsync(page, ResultFlowPageRenderMode.Continuation, CancellationToken.None)
			.ConfigureAwait(false);

		AnsiStyling().Replace(raw, string.Empty).Should().MatchRegex(@"name\s+id");
	}

	[TestMethod]
	[Description("Rows that are all empty objects have no keys to make columns of: each reads {} rather than being dropped from a table with no columns.")]
	public async Task When_EveryRowIsAnEmptyObject_Then_EachReadsAsAnEmptyObject()
	{
		var output = await RenderAsync(new JsonArray(new JsonObject(), new JsonObject())).ConfigureAwait(false);

		output.Split(Environment.NewLine).Should().Equal("{}", "{}");
	}

	[TestMethod]
	[Description("Cells are looked up ordinally, the way the columns were collected: a case-insensitive row must not also fill the 'name' column from its 'Name' key.")]
	public async Task When_ARowIsCaseInsensitive_Then_ItsCellsStillMatchKeysOrdinally()
	{
		var caseInsensitive = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }) { ["Name"] = "upper" };

		var output = await RenderAsync(new JsonArray(caseInsensitive, new JsonObject { ["name"] = "lower" })).ConfigureAwait(false);

		output.Split("\"upper\"").Should().HaveCount(2, "the value belongs to its own column only");
	}

	[TestMethod]
	[Description("A page of JSON nulls is recognized from its declared item type: with no non-null item to go on, it would otherwise read as having no results.")]
	public async Task When_APageOfJsonNullsIsRendered_Then_EachItemReadsNull()
	{
		var page = new ReplPage<JsonNode?>(
			[null, null],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));

		var output = await RenderAsync(page).ConfigureAwait(false);

		output.Split(Environment.NewLine).Should().Equal("null", "null");
	}

	[TestMethod]
	[Description("Through the pager, which pins the first page's header and drops a repeated one (a bold first line counts as one): a continuation page with other keys must keep its own header, or its rows sit under headings that are not theirs.")]
	public async Task When_ThePagerAppendsAPageWithOtherKeys_Then_ItsRowsKeepTheirOwnHeader()
	{
		var transformer = CreateAnsiTransformer();
		var first = await transformer.TransformPageAsync(
			SingleRowPage(new JsonObject { ["id"] = 1, ["name"] = "a" }), ResultFlowPageRenderMode.Initial, CancellationToken.None)
			.ConfigureAwait(false);
		var next = await transformer.TransformPageAsync(
			SingleRowPage(new JsonObject { ["name"] = "b", ["id"] = 2 }), ResultFlowPageRenderMode.Continuation, CancellationToken.None)
			.ConfigureAwait(false);

		var session = new PagerSession(first, hasMorePayload: true, maxBufferedLines: 100);
		session.Append(next, hasMorePayload: false, containsPresentationChrome: false);

		var lines = session.Lines.Select(line => AnsiStyling().Replace(line, string.Empty)).ToList();
		var row = lines.FindIndex(line => line.Contains("\"b\"", StringComparison.Ordinal));
		row.Should().BePositive();
		lines.Take(row).Should().Contain(line => line.Contains("name", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Through the pager, a continuation page with the same keys at other widths repeats the pinned header, so it adds its data row only.")]
	public async Task When_ThePagerAppendsAPageWithTheSameKeys_Then_OnlyItsRowIsAdded()
	{
		var transformer = CreateAnsiTransformer();
		var first = await transformer.TransformPageAsync(
			SingleRowPage(new JsonObject { ["id"] = 1, ["name"] = "a" }), ResultFlowPageRenderMode.Initial, CancellationToken.None)
			.ConfigureAwait(false);
		var next = await transformer.TransformPageAsync(
			SingleRowPage(new JsonObject { ["id"] = 22, ["name"] = "bbbbbb" }), ResultFlowPageRenderMode.Continuation, CancellationToken.None)
			.ConfigureAwait(false);

		var session = new PagerSession(first, hasMorePayload: true, maxBufferedLines: 100);
		session.Append(next, hasMorePayload: false, containsPresentationChrome: false);

		session.Lines.Should().HaveCount(2, "the first page's row and the continuation's row, with no repeated header");
		AnsiStyling().Replace(session.Lines[1], string.Empty).Should().Contain("\"bbbbbb\"");
	}

	// The ANSI pager case, where the bold header line is what the pager detects and pins. Forced, so these
	// tests do not depend on whether the console running them supports ANSI.
	private static SpectreHumanOutputTransformer CreateAnsiTransformer() =>
		new(
			() => new HumanRenderSettings(Width: 120, UseAnsi: true, Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)),
			new OutputOptions { AnsiMode = AnsiMode.Always });

	private static ReplPage<JsonObject> SingleRowPage(JsonObject row) =>
		new([row], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1));

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
