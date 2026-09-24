using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Repl.Spectre;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed partial class Given_OutputFormatting
{
	[TestMethod]
	[Description("Regression guard: verifies rendering human string result so that output contains raw text.")]
	public void When_RenderingHumanStringResult_Then_OutputContainsRawText()
	{
		var sut = ReplApp.Create();
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["hello"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("world");
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering json result so that output is serialized json.")]
	public void When_RenderingJsonResult_Then_OutputIsSerializedJson()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--json"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"id\": 42");
		output.Text.Should().Contain("\"name\": \"Alice\"");
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering xml result so that output is serialized xml.")]
	public void When_RenderingXmlResult_Then_OutputIsSerializedXml()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--xml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("<Contact>");
		output.Text.Should().Contain("<id>42</id>");
		output.Text.Should().Contain("<name>Alice</name>");
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering xml collection so that top-level item nodes use runtime item type names.")]
	public void When_RenderingXmlCollection_Then_TopLevelItemsUseRuntimeTypeNames()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => new[]
		{
			new Contact(1, "Alice"),
			new Contact(2, "Bob"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--xml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("<items>");
		output.Text.Should().Contain("<Contact>");
		output.Text.Should().NotContain("<item>");
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering heterogeneous xml collection so that each item uses its own runtime type name.")]
	public void When_RenderingXmlHeterogeneousCollection_Then_EachItemUsesItsTypeName()
	{
		var sut = ReplApp.Create();
		sut.Map("mix", () => new object[]
		{
			new Contact(1, "Alice"),
			new ContactNote("vip"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["mix", "--xml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("<Contact>");
		output.Text.Should().Contain("<ContactNote>");
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering yaml result so that output is serialized yaml.")]
	public void When_RenderingYamlResult_Then_OutputIsSerializedYaml()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--yaml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("id: 42");
		output.Text.Should().Contain("name: 'Alice'");
	}

	[TestMethod]
	[Description("Regression guard: verifies using yml alias so that yaml transformer is selected.")]
	public void When_RenderingYamlResultWithYmlAlias_Then_OutputIsSerializedYaml()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--yml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("id: 42");
		output.Text.Should().Contain("name: 'Alice'");
	}

	[TestMethod]
	[Description("Regression guard: verifies using output selector so that xml transformer is selected.")]
	public void When_RenderingXmlResultWithOutputSelector_Then_OutputIsSerializedXml()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--output:xml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("<Contact>");
		output.Text.Should().Contain("<id>42</id>");
		output.Text.Should().Contain("<name>Alice</name>");
	}

	[TestMethod]
	[Description("Regression guard: verifies output aliases are configurable so that custom alias can map to markdown format.")]
	public void When_RenderingWithCustomAlias_Then_AliasMappedTransformerIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Output.AddTransformer("mdcustom", new ConstantTransformer("markdown-render"));
			options.Output.AddAlias("pretty", "mdcustom");
		});
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--pretty"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("markdown-render");
	}

	[TestMethod]
	[Description("Regression guard: verifies markdown flag is an output alias so that built-in markdown transformer is selected without parser hardcoding.")]
	public void When_RenderingWithMarkdownAlias_Then_MarkdownTransformerIsSelected()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--markdown"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("| Field | Value |");
		output.Text.Should().Contain("| Id | 42 |");
	}

	[TestMethod]
	[Description("Regression guard: verifies markdown rendering of IReplResult details so that result message and details table are both visible.")]
	public void When_RenderingMarkdownForResultWithDetails_Then_MessageAndDetailsAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => Results.Success("Contact found.", new Contact(42, "Alice")));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--markdown", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Success: Contact found.");
		output.Text.Should().Contain("| Field | Value |");
		output.Text.Should().Contain("| Id | 42 |");
	}

	[TestMethod]
	[Description("Regression guard: verifies markdown rendering for object collections so that tabular markdown is produced instead of runtime type names.")]
	public void When_RenderingObjectCollectionInMarkdown_Then_TableMarkdownIsProduced()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => new[]
		{
			new ContactMarkdownRow(1, "Alice Martin", "alice@example.com"),
			new ContactMarkdownRow(2, "Bob Tremblay", "bob@example.com"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--markdown", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("| Id | Name | Email |");
		output.Text.Should().Contain("| --- | --- | --- |");
		output.Text.Should().Contain("| 1 | Alice Martin | alice@example.com |");
		output.Text.Should().NotContain("System.Collections.Generic.List");
	}

	[TestMethod]
	[Description("Regression guard: verifies paged results render their current page and continuation hint in human output.")]
	public void When_RenderingPagedResultInHuman_Then_ItemsAndContinuationAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.Page(
				new[]
				{
					new ContactRow("Alice Martin", "alice@example.com"),
					new ContactRow("Bob Tremblay", "bob@example.com"),
				},
				nextCursor: "page-2",
				totalCount: 3));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "list", "--result:page-size=2", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Alice Martin");
		output.Text.Should().Contain("Bob Tremblay");
		output.Text.Should().Contain("Showing 2 of 3.");
		output.Text.Should().Contain("Next data page:");
		output.Text.Should().Contain("--result:cursor page-2");
	}

	[TestMethod]
	[Description("Regression guard: verifies human page sources continue interactively instead of asking users to rerun with a cursor.")]
	public void When_RenderingPageSourceInHumanPager_Then_SpaceFetchesNextPageWithoutCursorRerun()
	{
		var sut = ReplApp.Create();
		var fetchedCursors = new List<string?>();
		var contacts = new[]
		{
			new ContactRow("Alice Martin", "alice@example.com"),
			new ContactRow("Bob Tremblay", "bob@example.com"),
		};

		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.CreateSource<ContactRow>((request, _) =>
			{
				fetchedCursors.Add(request.Cursor);
				var offset = string.Equals(request.Cursor, "1", StringComparison.Ordinal) ? 1 : 0;
				var items = contacts.Skip(offset).Take(request.PageSize).ToArray();
				var nextOffset = offset + items.Length;
				var nextCursor = nextOffset < contacts.Length ? "1" : null;
				return ValueTask.FromResult(new ReplPage<ContactRow>(
					items,
					new ReplPageInfo(
						request.Cursor,
						nextCursor,
						contacts.Length,
						request.PageSize)));
			}));

		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		ReplSessionIO.KeyReader = new QueueKeyReader([Key(ConsoleKey.Spacebar, ' ')]);
		ReplSessionIO.WindowSize = (100, 20);

		var exitCode = sut.Run(["contact", "list", "--result:page-size=1", "--no-logo"]);

		exitCode.Should().Be(0);
		fetchedCursors.Should().Equal(null, "1");
		var text = output.ToString();
		text.Should().Contain("Alice Martin");
		text.Should().Contain("Bob Tremblay");
		text.Should().NotContain("rerun with --result:cursor");
		text.Split("Name", StringSplitOptions.None).Should().HaveCount(2);
		text.Should().NotContain("Showing ");
	}

	[TestMethod]
	[Description("A JSON page source in the human pager: a page with the same keys at other widths adds its row only, and a page with other keys shows its own header, so no row sits under headings that are not its own.")]
	public void When_PagingJsonRowsWhoseKeysChange_Then_EachHeaderShowsOnceForItsRows()
	{
		var sut = ReplApp.Create();
		JsonObject[] rows =
		[
			new() { ["id"] = 1, ["name"] = "a" },
			new() { ["id"] = 22, ["name"] = "bbbb" },
			new() { ["name"] = "c", ["id"] = 3 },
		];
		sut.Map("rows", (IReplPagingContext paging) =>
			paging.CreateSource<JsonObject>((request, _) =>
			{
				var index = request.Cursor is null ? 0 : int.Parse(request.Cursor, CultureInfo.InvariantCulture);
				var next = index + 1 < rows.Length ? (index + 1).ToString(CultureInfo.InvariantCulture) : null;
				return ValueTask.FromResult(new ReplPage<JsonObject>(
					[rows[index]],
					new ReplPageInfo(request.Cursor, next, rows.Length, request.PageSize)));
			}));

		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		ReplSessionIO.KeyReader = new QueueKeyReader([.. Enumerable.Repeat(Key(ConsoleKey.Spacebar, ' '), 4)]);
		ReplSessionIO.WindowSize = (100, 20);

		var exitCode = sut.Run(["rows", "--result:page-size=1", "--no-logo"]);

		exitCode.Should().Be(0);
		var lines = StripAnsi(output.ToString()).Split(Environment.NewLine);
		lines.Count(line => IsHeader(line, "id", "name")).Should().Be(1, "the second page repeats the first one's columns");
		lines.Count(line => IsHeader(line, "name", "id")).Should().Be(1, "the third page names other columns");
		var third = Array.FindIndex(lines, line => line.Contains("\"c\"", StringComparison.Ordinal));
		third.Should().BePositive();
		lines.Take(third).Last(line => IsHeader(line, "id", "name") || IsHeader(line, "name", "id"))
			.Should().Match(line => IsHeader(line, "name", "id"), "the third page's rows sit under its own header");
	}

	[TestMethod]
	[Description("A JSON array returned whole and paged: a row that reads like the header is data, so every row reaches the screen rather than being dropped as a repeated header.")]
	public void When_PagingAJsonArrayWhoseRowsReadLikeItsHeader_Then_EveryRowIsShown()
	{
		var sut = ReplApp.Create();
		sut.Map("ones", () => new JsonArray([.. Enumerable.Range(0, 10).Select(_ => (JsonNode)new JsonObject { ["1"] = 1 })]));

		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		ReplSessionIO.KeyReader = new QueueKeyReader([.. Enumerable.Repeat(Key(ConsoleKey.Spacebar, ' '), 10)]);
		ReplSessionIO.WindowSize = (100, 8);

		var exitCode = sut.Run(["ones", "--no-logo"]);

		exitCode.Should().Be(0);
		StripAnsi(output.ToString()).Split(Environment.NewLine).Count(line => string.Equals(line.Trim(), "1", StringComparison.Ordinal))
			.Should().Be(11, "the header, then each of the ten rows");
	}

	[TestMethod]
	[Description("A long page returned whole is paged without a way to fetch the next one, so its footer, which says how to rerun for it, stays in view.")]
	public void When_PagingALongPageReturnedWhole_Then_ItsRerunFooterIsShown()
	{
		var sut = ReplApp.Create();
		sut.Map("ones", () => new ReplPage<JsonObject>(
			[.. Enumerable.Range(1, 10).Select(i => new JsonObject { ["id"] = i })],
			new ReplPageInfo(Cursor: null, NextCursor: "next", TotalCount: 20, PageSize: 10)));

		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		ReplSessionIO.KeyReader = new QueueKeyReader([.. Enumerable.Repeat(Key(ConsoleKey.Spacebar, ' '), 10)]);
		ReplSessionIO.WindowSize = (100, 8);

		var exitCode = sut.Run(["ones", "--no-logo"]);

		exitCode.Should().Be(0);
		StripAnsi(output.ToString()).Should().Contain("Showing 10 of 20. Next data page: rerun with");
	}

	private static bool IsHeader(string line, params string[] columns) =>
		line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(columns, StringComparer.Ordinal);

	[TestMethod]
	[Description("Regression guard: verifies paging.CreateSource receives the caller cursor and suggested page size through the first source request.")]
	public void When_PageSourceIsCreatedFromPagingContext_Then_FirstRequestUsesCurrentPagingIntent()
	{
		var sut = ReplApp.Create();
		ReplPageRequest? capturedRequest = null;

		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.CreateSource<ContactRow>((request, _) =>
			{
				capturedRequest = request;
				return ValueTask.FromResult(request.Page(
					[new ContactRow("Alice Martin", "alice@example.com")]));
			}));

		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["contact", "list", "--result:page-size=7", "--result:cursor=page-2", "--no-logo"]);

		exitCode.Should().Be(0);
		capturedRequest.Should().NotBeNull();
		capturedRequest!.Cursor.Should().Be("page-2");
		capturedRequest.PageSize.Should().Be(7);
	}

	[TestMethod]
	[Description("Regression guard: verifies paged results serialize to a clean JSON envelope for automation.")]
	public void When_RenderingPagedResultInJson_Then_ItemsAndPageInfoAreSerialized()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.Page(
				new[]
				{
					new Contact(1, "Alice"),
				},
				nextCursor: "page-2",
				totalCount: 2));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "list", "--json", "--result:page-size=1", "--result:cursor=start", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"items\"");
		output.Text.Should().Contain("\"pageInfo\"");
		output.Text.Should().Contain("\"nextCursor\": \"page-2\"");
		output.Text.Should().Contain("\"cursor\": \"start\"");
		output.Text.Should().Contain("\"totalCount\": 2");
		output.Text.Should().NotContain("itemType");
		output.Text.Should().NotContain("untypedItems");
	}

	[TestMethod]
	[Description("Regression guard: verifies paged results render their current page in markdown output.")]
	public void When_RenderingPagedResultInMarkdown_Then_ItemsAndContinuationAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.Page(
				new[]
				{
					new ContactMarkdownRow(1, "Alice Martin", "alice@example.com"),
				},
				nextCursor: "page-2"));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "list", "--markdown", "--result:page-size=1", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("| Id | Name | Email |");
		output.Text.Should().Contain("| 1 | Alice Martin | alice@example.com |");
		output.Text.Should().Contain("`--result:cursor page-2`");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting unknown output format so that user gets a clear error and non-zero exit code.")]
	public void When_RenderingWithUnknownFormat_Then_ClearErrorIsShownAndExitCodeIsNonZero()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(
			() => sut.Run(["contact", "show", "--output:toml"]));

		output.ExitCode.Should().Be(2);

		// On stderr, not stdout: the refusal is a framework diagnostic, and a headless caller parses
		// stdout as the payload it asked for.
		output.StdErr.Should().Contain("Error: unknown output format 'toml'.");
		output.StdOut.Should().NotContain("unknown output format");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns null so that no output is rendered.")]
	public void When_HandlerReturnsNull_Then_NoOutputIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("noop", () => (object?)null);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["noop", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Trim().Should().BeEmpty();
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering human object with annotations so that display rules are applied.")]
	public void When_RenderingHumanObjectWithAnnotations_Then_DisplayRulesAreApplied()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new AnnotatedContact(
			Id: 42,
			Name: "Alice Martin",
			Email: "alice@example.com",
			Phone: null,
			InternalNotes: "hidden"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		var expected = string.Join(
			Environment.NewLine,
			"#        : 42",
			"Full Name: Alice Martin",
			"Email    : alice@example.com",
			"Phone    : -");
		output.Text.TrimEnd().Should().Be(expected);
	}

	[TestMethod]
	[Description("Regression guard: verifies rendering human nested values so that object and collection are summarized.")]
	public void When_RenderingHumanNestedValues_Then_ObjectAndCollectionAreSummarized()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new NestedContact(
			"Alice",
			new Address("742 Evergreen Terrace", "Springfield"),
			[new Phone("mobile", "123"), new Phone("work", "456")]));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		var expected = string.Join(
			Environment.NewLine,
			"Name   : Alice",
			"Address: 742 Evergreen Terrace, Springfield",
			"Phones :",
			"  Type    Number",
			"  ------  ------",
			"  mobile  123",
			"  work    456");
		output.Text.TrimEnd().Should().Be(expected);
	}

	[TestMethod]
	[Description("Regression guard: verifies preferred human table width is enforced so that each rendered row stays within the configured width.")]
	public void When_PreferredRenderWidthIsConfigured_Then_TableRowsFitWithinWidth()
	{
		const int width = 36;
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Output.PreferredWidth = width;
			options.Output.FallbackWidth = width;
		});
		sut.Map("contact list", () => new[]
		{
			new ContactRow("Alice Martin", "alice.martin.super.long@example.com"),
			new ContactRow("Bob Tremblay", "bob@example.com"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));
		var lines = output.Text
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		output.ExitCode.Should().Be(0);
		lines.Should().OnlyContain(line => line.Length <= width);
		output.Text.Should().Contain("...");
	}

	[TestMethod]
	[Description("Regression guard: verifies width changes are applied to future renders so that table truncation adapts to terminal resize scenarios.")]
	public void When_PreferredRenderWidthChanges_Then_SubsequentOutputUsesNewWidth()
	{
		const int wideWidth = 80;
		const int narrowWidth = 32;
		OutputOptions? configuredOutput = null;
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Output.PreferredWidth = wideWidth;
			options.Output.FallbackWidth = wideWidth;
			configuredOutput = options.Output;
		});
		sut.Map("contact list", () => new[]
		{
			new ContactRow("Alice Martin", "alice.martin.super.long@example.com"),
		});

		var wide = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));
		configuredOutput!.PreferredWidth = narrowWidth;
		configuredOutput.FallbackWidth = narrowWidth;
		var narrow = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));

		wide.ExitCode.Should().Be(0);
		narrow.ExitCode.Should().Be(0);
		wide.Text.Should().NotContain("...");
		narrow.Text.Should().Contain("...");
	}

	[TestMethod]
	[Description("Regression guard: verifies ANSI forced human rendering so that table headers can be highlighted without a separator row.")]
	public void When_AnsiModeIsAlways_Then_HumanTableUsesAnsiHeaderWithoutSeparator()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Output.AnsiMode = AnsiMode.Always;
			options.Output.PreferredWidth = 120;
		});
		sut.Map("contact list", () => new[]
		{
			new ContactRow("Alice Martin", "alice@example.com"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));
		var lines = output.Text.TrimEnd().Split(Environment.NewLine, StringSplitOptions.None);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\u001b[");
		lines.Length.Should().Be(2);
	}

	[TestMethod]
	[Description("Regression guard: verifies ANSI custom palette provider is used so that color mapping can be configured by host applications.")]
	public void When_CustomAnsiPaletteProviderIsConfigured_Then_ProviderStylesAreUsed()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Output.AnsiMode = AnsiMode.Always;
			options.Output.PaletteProvider = new TestPaletteProvider();
		});
		sut.Map("contact list", () => new[]
		{
			new ContactRow("Alice Martin", "alice@example.com"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\u001b[35m");
	}

	[TestMethod]
	[Description("Regression guard: verifies ANSI human object rendering uses styled labels so key/value blocks are easier to scan interactively.")]
	public void When_AnsiModeIsAlwaysAndObjectRendered_Then_LabelsAreColorized()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\u001b[");
		output.Text.Should().Contain("Id");
		output.Text.Should().Contain("Name");
		output.Text.Should().Contain(": 42");
		output.Text.Should().Contain(": Alice");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive JSON output is ANSI colorized so that machine formats remain readable in REPL sessions.")]
	public void When_InteractiveJsonOutputAndAnsiEnabled_Then_JsonPayloadIsColorized()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.CaptureWithInput("contact show --json\nexit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\u001b[");
	}

	[TestMethod]
	[Description("Regression guard: verifies non-interactive JSON output is not ANSI colorized so that piped output stays clean.")]
	public void When_NonInteractiveJsonOutputAndAnsiEnabled_Then_JsonPayloadRemainsPlain()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"id\": 42");
		output.Text.Should().NotContain("\u001b[");
	}

	[TestMethod]
	[Description("Regression guard: verifies Spectre becomes the default output format and renders objects without boxed chrome.")]
	public void When_UsingSpectreConsole_Then_ObjectOutputStaysLightweight()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Id");
		output.Text.Should().Contain("Name");
		output.Text.Should().Contain("42");
		output.Text.Should().Contain("Alice");
		output.Text.Should().NotContain("╭");
		output.Text.Should().NotContain("│");
	}

	[TestMethod]
	[Description("Regression guard: verifies Spectre output uses configured render width so PreferredWidth applies consistently.")]
	public void When_SpectreOutputAndPreferredRenderWidthIsConfigured_Then_TableRowsFitWithinWidth()
	{
		const int width = 36;
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Options(options =>
		{
			options.Output.PreferredWidth = width;
			options.Output.FallbackWidth = width;
		});
		sut.Map("contact list", () => new[]
		{
			new ContactRow("Alice Martin", "alice.martin.super.long@example.com"),
			new ContactRow("Bob Tremblay", "bob@example.com"),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--no-logo"]));
		var lines = output.Text
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(StripAnsi);

		output.ExitCode.Should().Be(0);
		lines.Should().OnlyContain(line => line.Length <= width);
		output.Text.Should().Contain("ong@example.com");
	}

	[TestMethod]
	[Description("Regression guard: verifies Spectre renders paged result pages with continuation metadata.")]
	public void When_SpectreOutputAndPagedResult_Then_ItemsAndContinuationAreRendered()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("contact list", (IReplPagingContext paging) =>
			paging.Page(
				new[]
				{
					new ContactRow("Alice Martin", "alice@example.com"),
				},
				nextCursor: "page-2",
				totalCount: 2));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["contact", "list", "--result:page-size=1", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Alice Martin");
		output.Text.Should().Contain("Showing 1 of 2.");
		output.Text.Should().Contain("--result:cursor page-2");
	}

	[TestMethod]
	[Description("Regression guard: verifies --human remains available even when Spectre is the default output format.")]
	public void When_UsingHumanAliasWithSpectreDefault_Then_ClassicHumanTransformerIsUsed()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--human", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.TrimEnd().Should().Be(
			string.Join(
				Environment.NewLine,
				"Id  : 42",
				"Name: Alice"));
	}

	private sealed record Contact(int Id, string Name);
	private sealed record ContactNote(string Note);

	private sealed record AnnotatedContact(
		[property: Display(Name = "#", Order = 0)] int Id,
		[property: Display(Name = "Full Name", Order = 1)] string Name,
		[property: Display(Order = 2)] string Email,
		[property: Display(Order = 3), DisplayFormat(NullDisplayText = "-")] string? Phone,
		[property: System.ComponentModel.Browsable(false)] string InternalNotes);

	private sealed record NestedContact(string Name, Address Address, IReadOnlyList<Phone> Phones);

	private sealed record Address(string Street, string City);

	private sealed record Phone(string Type, string Number);

	private sealed record ContactRow(string Name, string Email);
	private sealed record ContactMarkdownRow(int Id, string Name, string Email);

	private sealed class ConstantTransformer(string value) : IOutputTransformer
	{
		public string Name => "constant";

		public ValueTask<string> TransformAsync(object? input, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value);
	}

	private sealed class TestPaletteProvider : IAnsiPaletteProvider
	{
		public AnsiPalette Create(ThemeMode themeMode) =>
			new(
				SectionStyle: "\u001b[36m",
				TableHeaderStyle: "\u001b[35m",
				CommandStyle: "\u001b[34m",
				DescriptionStyle: "\u001b[33m");
	}

	private sealed class QueueKeyReader(IEnumerable<ConsoleKeyInfo> keys) : IReplKeyReader
	{
		private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

		public bool KeyAvailable => _keys.Count > 0;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			return _keys.TryDequeue(out var key)
				? ValueTask.FromResult(key)
				: throw new InvalidOperationException("No key available in QueueKeyReader.");
		}
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar) =>
		new(keyChar, key, shift: false, alt: false, control: false);

	private static string StripAnsi(string value) =>
		BuildAnsiEscapeRegex().Replace(value, string.Empty);

	[GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.None, matchTimeoutMilliseconds: 50)]
	private static partial Regex BuildAnsiEscapeRegex();
}









