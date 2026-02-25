using System.ComponentModel.DataAnnotations;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_OutputFormatting
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
	[Description("Regression guard: verifies requesting unknown output format so that user gets a clear error and non-zero exit code.")]
	public void When_RenderingWithUnknownFormat_Then_ClearErrorIsShownAndExitCodeIsNonZero()
	{
		var sut = ReplApp.Create();
		sut.Map("contact show", () => new Contact(42, "Alice"));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "show", "--output:toml"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: unknown output format 'toml'.");
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

}









