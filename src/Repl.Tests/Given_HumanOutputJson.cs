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

	[TestMethod]
	[Description("A continuation page carries its own JSON header: its rows' keys can come in another order, or be other keys, than the first page's, whose headings would then mislabel its values.")]
	public async Task When_AContinuationPageHasOtherKeys_Then_ItCarriesItsOwnHeader()
	{
		var page = new ReplPage<JsonObject>(
			[new JsonObject { ["name"] = "b", ["id"] = 2 }],
			new ReplPageInfo(Cursor: "1", NextCursor: null, TotalCount: null, PageSize: 1));

		var rendered = await CreateTransformer().RenderPageAsync(page, CancellationToken.None);

		rendered.Text.Split(Environment.NewLine)[0].Should().MatchRegex(@"^name\s+id\s*$");
		rendered.Layout.HeaderLineCount.Should().Be(2, "the header line and its separator");
		rendered.Layout.Columns.Should().Equal("name", "id");
	}

	[TestMethod]
	[Description("Rows that are all empty objects have no keys to make columns of: each reads {} rather than being dropped from a table with no columns.")]
	public async Task When_EveryRowIsAnEmptyObject_Then_EachReadsAsAnEmptyObject()
	{
		(await RenderAsync(new JsonArray(new JsonObject(), new JsonObject()))).Split(Environment.NewLine)
			.Should().Equal("{}", "{}");
	}

	[TestMethod]
	[Description("Cells are looked up ordinally, the way the columns were collected: a case-insensitive row must not also fill the 'name' column from its 'Name' key.")]
	public async Task When_ARowIsCaseInsensitive_Then_ItsCellsStillMatchKeysOrdinally()
	{
		var caseInsensitive = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }) { ["Name"] = "upper" };

		var output = await RenderAsync(new JsonArray(caseInsensitive, new JsonObject { ["name"] = "lower" }));

		var lines = output.Split(Environment.NewLine);
		lines[0].Should().MatchRegex(@"^Name\s+name\s*$");
		var upperRow = lines.Single(line => line.Contains("\"upper\"", StringComparison.Ordinal));
		upperRow.TrimEnd().Should().Be("\"upper\"", "its value sits under Name only, leaving the name cell empty");
		var lowerRow = lines.Single(line => line.Contains("\"lower\"", StringComparison.Ordinal));
		lowerRow.Should().MatchRegex(@"^\s+""lower""\s*$", "its value sits under name only, leaving the Name cell empty");
	}

	[TestMethod]
	[Description("A page of JSON nulls is recognized from its declared item type: with no non-null item to go on, it would otherwise render blank rows.")]
	public async Task When_APageOfJsonNullsIsRendered_Then_EachItemReadsNull()
	{
		var page = new ReplPage<JsonNode?>(
			[null, null],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));

		(await RenderAsync(page)).Split(Environment.NewLine).Should().Equal("null", "null");
	}

	[TestMethod]
	[Description("A page of nullable JsonElement is JSON too: its declared item type is Nullable<JsonElement>, not JsonElement itself.")]
	public async Task When_APageOfNullableJsonElementNullsIsRendered_Then_EachItemReadsNull()
	{
		var page = new ReplPage<JsonElement?>(
			[null, null],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));

		(await RenderAsync(page)).Split(Environment.NewLine).Should().Equal("null", "null");
	}

	[TestMethod]
	[Description("A JSON null among object rows, as a JsonElement or a CLR null, makes every row a literal: as an empty table row it would read as no row, and as the last one it would vanish with the payload's trailing blank line.")]
	public async Task When_APageOfJsonObjectsHasANullRow_Then_EveryRowReadsAsALiteral()
	{
		using var document = JsonDocument.Parse("""[{"id":1},null]""");
		var elements = new ReplPage<JsonElement>(
			[.. document.RootElement.EnumerateArray()],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));
		var nodes = new ReplPage<JsonNode?>(
			[new JsonObject { ["id"] = 1 }, null],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));

		(await RenderAsync(elements)).Split(Environment.NewLine).Should().Equal("""{"id":1}""", "null");
		(await RenderAsync(nodes)).Split(Environment.NewLine).Should().Equal("""{"id":1}""", "null");
	}

	[TestMethod]
	[Description("An empty object among keyed rows renders as blank as a null would, so it makes every row a literal too.")]
	public async Task When_APageOfJsonObjectsHasAnEmptyObject_Then_EveryRowReadsAsALiteral()
	{
		var output = await RenderAsync(new JsonArray(new JsonObject { ["id"] = 1 }, new JsonObject()));

		output.Split(Environment.NewLine).Should().Equal("""{"id":1}""", "{}");
	}

	[TestMethod]
	[Description("Through the pager, keys A, then B, then A again: each page shows its header, since the one in view before it names other columns.")]
	public async Task When_ThePagerAppendsKeysThatSwitchBack_Then_EachPageShowsItsHeader()
	{
		var session = await PageThroughAsync(
			CreateTransformer(),
			SingleRowPage(new JsonObject { ["id"] = 1, ["name"] = "a" }),
			SingleRowPage(new JsonObject { ["name"] = "b", ["id"] = 2 }),
			SingleRowPage(new JsonObject { ["id"] = 3, ["name"] = "c" }));

		session.Lines.Should().HaveCount(7, "the first row, then a header, its separator and a row for each later page");
		session.Lines[4].Should().MatchRegex(@"^id\s+name\s*$");
	}

	[TestMethod]
	[Description("Through the pager, a first page with no header pins none; the next table page shows its header, and the one after, with the same columns, adds its row only.")]
	public async Task When_TheFirstPageHasNoHeader_Then_TheFirstTablePageShowsItOnce()
	{
		var session = await PageThroughAsync(
			CreateTransformer(),
			new ReplPage<JsonNode?>([null], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1)),
			SingleRowPage(new JsonObject { ["id"] = 1 }),
			SingleRowPage(new JsonObject { ["id"] = 2 }));

		session.HeaderLines.Should().BeEmpty();
		session.Lines.Should().Equal("null", "id", "--", "1", "2");
	}

	[TestMethod]
	[Description("Through the pager, a page of rows of another type shows its own header: a type's table renders its header on every page, and the pager drops only a repeat.")]
	public async Task When_ThePagerAppendsRowsOfAnotherType_Then_TheirHeaderIsShown()
	{
		var session = await PageThroughAsync(
			CreateTransformer(),
			new ReplPage<object>([new Holder("h1", new JsonObject())], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1)),
			new ReplPage<object>([new Holder("h2", new JsonObject())], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1)),
			new ReplPage<object>([new Owner("octo")], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1)));

		session.Lines.Should().HaveCount(5, "the two holders' rows, then the owner's header, separator and row");
		session.Lines[2].Should().MatchRegex(@"^Login\s*$");
	}

	[TestMethod]
	[Description("A line separator (U+2028) in a display name breaks the line for the pager as a line feed does, so it reads as a space too.")]
	public async Task When_ADisplayNameHasALineSeparator_Then_TheHeaderStaysOnOneLine()
	{
		var rendered = await CreateTransformer().RenderAsync(new[] { new Separated("now") }, CancellationToken.None);

		var session = new PagerSession(rendered.Text, hasMorePayload: false, maxBufferedLines: 100, rendered.Layout);
		session.HeaderLines.Should().HaveCount(2, "the header line and its separator");
		session.HeaderLines[0].Should().MatchRegex("^Created At *$");
		session.Lines.Should().ContainSingle();
	}

	[TestMethod]
	[Description("A result whose details are a page declares that page's footer, its last line, and no header: the message comes first.")]
	public async Task When_AResultCarriesAPage_Then_ItsFooterIsDeclaredAndLast()
	{
		var page = new ReplPage<JsonObject>(
			[new JsonObject { ["id"] = 1 }],
			new ReplPageInfo(Cursor: null, NextCursor: "2", TotalCount: 5, PageSize: 1));

		var rendered = await CreateTransformer().RenderAsync(Results.Success("Found", page), CancellationToken.None);

		rendered.Text.Split(Environment.NewLine)[^1].Should().StartWith("Showing 1 of 5.");
		rendered.Layout.HeaderLineCount.Should().Be(0);
		rendered.Layout.FooterLineCount.Should().Be(1);
	}

	[TestMethod]
	[Description("A display name with a line break still makes a one-line header: the declared height must match what is rendered.")]
	public async Task When_ADisplayNameHasALineBreak_Then_TheHeaderStaysOnOneLine()
	{
		var rendered = await CreateTransformer().RenderAsync(new[] { new Stamped("now") }, CancellationToken.None);

		// Split the way the pager splits, at any line break, lone ones included.
		var lines = rendered.Text.ReplaceLineEndings("\n").Split('\n');
		lines.Should().HaveCount(3, "the header, its separator and the row");
		lines[0].Should().MatchRegex("^Created At *$");
		rendered.Layout.HeaderLineCount.Should().Be(2);
	}

	[TestMethod]
	[Description("Through the pager, a page whose last row is a JSON null keeps that row: it reads null rather than being trimmed as a trailing blank line.")]
	public async Task When_ThePagerReceivesAPageEndingInANullRow_Then_TheRowIsKept()
	{
		var page = new ReplPage<JsonNode?>(
			[new JsonObject { ["id"] = 1 }, null],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));

		var session = await PageThroughAsync(CreateTransformer(), page, SingleRowPage(new JsonObject { ["id"] = 2 }));

		session.Lines.Should().Contain("null");
	}

	[TestMethod]
	[Description("Through the pager, a column named 1 holding 1 renders its header and its row alike; the row is data, and must not be dropped as a repeated header.")]
	public async Task When_ARowReadsLikeTheHeader_Then_ThePagerKeepsIt()
	{
		var session = await PageThroughAsync(
			CreateTransformer(),
			SingleRowPage(new JsonObject { ["1"] = 1 }),
			SingleRowPage(new JsonObject { ["1"] = 1 }));

		session.Lines.Should().Equal("1", "1");
	}

	[TestMethod]
	[Description("Through the ANSI pager, keys 'a  b' / 'c' and 'a' / 'b  c' render headers that read alike but name other columns: the continuation keeps its own.")]
	public async Task When_TheAnsiPagerAppendsKeysThatRegroupTheirSpaces_Then_TheContinuationKeepsItsHeader()
	{
		var session = await PageThroughAsync(
			CreateTransformer(useAnsi: true),
			SingleRowPage(new JsonObject { ["a  b"] = 1, ["c"] = 2 }),
			SingleRowPage(new JsonObject { ["a"] = 3, ["b  c"] = 4 }));

		session.Lines.Should().HaveCount(3, "the first row, then the continuation's own header and its row");
	}

	[TestMethod]
	[Description("A property declared as JSON holding null is a JSON null, as --json writes it: it reads null, not an empty value, both as a field and as a table cell.")]
	public async Task When_AJsonTypedPropertyIsNull_Then_ItReadsNull()
	{
		(await RenderAsync(new NullableHolder("h1", Payload: null))).Should().MatchRegex(@"Payload\s*:\s*null");

		var page = new ReplPage<NullableHolder>(
			[new NullableHolder("a", Payload: null), new NullableHolder("b", new JsonObject { ["x"] = 1 })],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 2));
		var rowA = (await RenderAsync(page)).Split(Environment.NewLine).Single(line => line.StartsWith('a'));
		rowA.TrimEnd().Should().EndWith("null");
	}

	[TestMethod]
	[Description("An explicit NullDisplayText still wins over the JSON null of a property declared as JSON.")]
	public async Task When_AJsonTypedPropertyHasANullDisplayText_Then_ItIsUsed()
	{
		(await RenderAsync(new DisplayedHolder(Payload: null))).Should().MatchRegex(@"Payload\s*:\s*\(none\)");
	}

	[TestMethod]
	[Description("With ANSI on, the table header has no separator line and a palette style; the pager must still pin it, or every JSON page, which carries its own header, would repeat it.")]
	public async Task When_AnAnsiPagerAppendsAPageWithTheSameKeys_Then_OnlyItsRowIsAdded()
	{
		var session = await PageThroughAsync(
			CreateTransformer(useAnsi: true),
			SingleRowPage(new JsonObject { ["id"] = 1, ["name"] = "a" }),
			SingleRowPage(new JsonObject { ["id"] = 22, ["name"] = "bbbbbb" }));

		session.HeaderLines.Should().ContainSingle("the styled header line is the pinned header");
		session.Lines.Should().HaveCount(2, "the first page's row and the continuation's row, with no repeated header");
	}

	[TestMethod]
	[Description("Line and paragraph separators (U+2028, U+2029) in a JSON key or string are escaped too: the pager breaks lines at them, so left raw they would split a one-line header or row.")]
	public async Task When_AJsonStringCarriesLineSeparators_Then_TheyAreEscaped()
	{
		var output = await RenderAsync(new JsonArray(new JsonObject { ["a\u2028b"] = "c\u2029d" }));

		output.Should().NotContain("\u2028").And.NotContain("\u2029");
		output.Should().Contain(@"a\u2028b").And.Contain(@"c\u2029d");
	}

	[TestMethod]
	[Description("A JsonElement may repeat a property name, which a JsonObject cannot hold: the last value is the one shown, as JavaScript reads it, rather than rendering failing.")]
	public async Task When_AJsonElementRepeatsAPropertyName_Then_TheLastValueIsShown()
	{
		using var document = JsonDocument.Parse("""{"id":1,"id":2,"nested":{"x":1,"x":3}}""");
		using var rows = JsonDocument.Parse("""[{"id":1,"id":2}]""");

		var output = await RenderAsync(document.RootElement);
		var table = await RenderAsync(rows.RootElement);

		output.Should().MatchRegex(@"id\s*:\s*2");
		output.Should().Contain("""{"x":3}""");
		table.Split(Environment.NewLine)[^1].Trim().Should().Be("2");
	}

	private sealed record NullableHolder(string Name, JsonNode? Payload);

	private sealed record DisplayedHolder(
		[property: System.ComponentModel.DataAnnotations.DisplayFormat(NullDisplayText = "(none)")] JsonNode? Payload);

	private sealed record Holder(string Name, JsonObject Payload);

	private sealed record Owner(string Login);

	private sealed record Separated([property: System.ComponentModel.DataAnnotations.Display(Name = "Created\u2028At")] string When);

	private sealed record Stamped([property: System.ComponentModel.DataAnnotations.Display(Name = "Created\nAt")] string When);

	private static ReplPage<JsonObject> SingleRowPage(JsonObject row) =>
		new([row], new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: null, PageSize: 1));

	private static HumanOutputTransformer CreateTransformer(bool useAnsi = false) =>
		new(() => new HumanRenderSettings(
			Width: 120,
			UseAnsi: useAnsi,
			Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));

	// Drives pages through a PagerSession the way the pager does, each with the layout its transformer declared.
	private static async Task<PagerSession> PageThroughAsync(HumanOutputTransformer transformer, params IReplPage[] pages)
	{
		var initial = await transformer.RenderPageAsync(pages[0], CancellationToken.None).ConfigureAwait(false);
		var session = new PagerSession(initial.Text, hasMorePayload: pages.Length > 1, maxBufferedLines: 100, initial.Layout);
		for (var i = 1; i < pages.Length; i++)
		{
			var next = await transformer.RenderPageAsync(pages[i], CancellationToken.None).ConfigureAwait(false);
			session.Append(next.Text, hasMorePayload: i < pages.Length - 1, containsPresentationChrome: false, next.Layout);
		}

		return session;
	}

	private static async Task<string> RenderAsync(object value) =>
		await CreateTransformer().TransformAsync(value, CancellationToken.None).ConfigureAwait(false);

	private static void AssertNoClrMembers(string output)
	{
		foreach (var member in ClrMembers)
		{
			output.Should().NotContain(member, "JSON data must not render the CLR members of its container type");
		}
	}
}
