namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_OptionsGroupBinding
{
	[ReplOptionsGroup]
	public class TestOutputOptions
	{
		[ReplOption(Aliases = ["-f"])]
		[System.ComponentModel.Description("Output format.")]
		public string Format { get; set; } = "text";

		[ReplOption(ReverseAliases = ["--no-verbose"])]
		public bool Verbose { get; set; }
	}

	[ReplOptionsGroup]
	public class HiddenOutputOptions
	{
		[ReplOption]
		public string Format { get; set; } = "text";

		[ReplOption(Name = "internal-token", Hidden = true)]
		public string? InternalToken { get; set; }
	}

	[ReplOptionsGroup]
	public class LegacyOutputOptions
	{
		[ReplOption(
			Name = "format",
			Aliases = ["--OUTPUT-FORMAT"],
			HiddenAliases = ["--output-format"],
			CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)]
		public string Format { get; set; } = "text";
	}

	[ReplOptionsGroup]
	public class TestPagingOptions
	{
		[ReplOption]
		public int Limit { get; set; } = 10;

		[ReplOption]
		public int Offset { get; set; }
	}

	[ReplOptionsGroup]
	public class PositionalSearchOptions
	{
		[ReplArgument(Mode = ReplParameterMode.OptionAndPositional)]
		public string Query { get; set; } = "";
	}

	[ReplOptionsGroup]
	public class NullableDefaultsOptions
	{
		[ReplOption]
		public int? Limit { get; set; } = 0;

		[ReplOption]
		public bool? Force { get; set; } = false;

		[ReplOption]
		public int Offset { get; set; }
	}

	[TestMethod]
	[Description("Regression guard: verifies named options bind to options group properties.")]
	public void When_UsingNamedOptionOnGroup_Then_PropertyBindsSuccessfully()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => output.Format);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--format", "json", "--no-logo"]));

		output.ExitCode.Should().Be(0, because: output.Text);
		output.Text.Should().Contain("json");
	}

	[TestMethod]
	[Description("Regression guard: verifies short alias binds to options group property.")]
	public void When_UsingShortAliasOnGroup_Then_PropertyBindsSuccessfully()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => output.Format);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "-f", "yaml", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yaml");
	}

	[TestMethod]
	[Description("Regression guard: verifies boolean flags bind to options group properties.")]
	public void When_UsingBoolFlagOnGroup_Then_PropertyBindsSuccessfully()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => output.Verbose.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--verbose", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("True");
	}

	[TestMethod]
	[Description("Regression guard: verifies reverse aliases bind to options group properties.")]
	public void When_UsingReverseAliasOnGroup_Then_PropertyBindsSuccessfully()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => output.Verbose.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--no-verbose", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("False");
	}

	[TestMethod]
	[Description("Regression guard: verifies default values are preserved when options are not provided.")]
	public void When_OptionNotProvided_Then_DefaultValueIsPreserved()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => output.Format);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("text");
	}

	[TestMethod]
	[Description("Regression guard: verifies options group and regular parameters bind correctly together.")]
	public void When_MixingGroupAndRegularParams_Then_BothBindCorrectly()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output, int limit) => $"{output.Format}:{limit}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--format", "json", "--limit", "5", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("json:5");
	}

	[TestMethod]
	[Description("Regression guard: verifies two different options groups bind independently.")]
	public void When_UsingTwoGroups_Then_BothBindIndependently()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output, TestPagingOptions paging) =>
			$"{output.Format}:{paging.Limit}:{paging.Offset}");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["list", "--format", "json", "--limit", "20", "--offset", "5", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("json:20:5");
	}

	[TestMethod]
	[Description("A hidden options-group property is omitted from help while remaining bindable when explicitly provided.")]
	public void When_OptionsGroupPropertyIsHidden_Then_HelpOmitsItAndExplicitInvocationStillBinds()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (HiddenOutputOptions options) => $"{options.Format}:{options.InternalToken}");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(
			["list", "--format", "json", "--internal-token", "secret", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--format");
		help.Text.Should().NotContain("--internal-token");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("json:secret");
	}

	[TestMethod]
	[Description("A hidden alias declared on an options-group property remains bindable while help and documentation expose only the canonical token.")]
	public void When_OptionsGroupPropertyAliasIsHidden_Then_OnlyParsingRetainsIt()
	{
		var sut = ReplApp.Create();
		sut.Map("list", static string (LegacyOutputOptions options) => options.Format);

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--output-format", "json", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--format");
		help.Text.Should().NotContain("--output-format");
		help.Text.Should().NotContain("--OUTPUT-FORMAT");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("json");
		option.Aliases.Should().Contain("--format");
		option.Aliases.Should().NotContain("--output-format");
	}

	[TestMethod]
	[Description("A fluent Hidden(false) override re-exposes an options-group property hidden by attribute.")]
	public void When_HiddenAttributeIsOverriddenFluentlyWithFalse_Then_OptionIsVisibleAgain()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (HiddenOutputOptions options) => "ok")
			.WithOption(nameof(HiddenOutputOptions.InternalToken), static option => option.Hidden(isHidden: false));

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--internal-token");
	}

	[TestMethod]
	[Description("Regression guard: a nullable group property initialized to the CLR default of its underlying type (int? = 0, bool? = false) is a deliberate default the binder preserves, so command help must advertise it — while implicit defaults of non-nullable properties stay hidden.")]
	public void When_NullableGroupPropertyInitializedToUnderlyingClrDefault_Then_HelpShowsDefault()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (NullableDefaultsOptions options) => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		var lines = output.Text.Split('\n');
		lines.Single(line => line.Contains("--limit")).Should().Contain("[default: 0]");
		lines.Single(line => line.Contains("--force")).Should().Contain("[default: False]");
		lines.Single(line => line.Contains("--offset")).Should().NotContain("[default:");
	}

	[TestMethod]
	[Description("Verifies a handler parameter that is not an option but is named like one of a group's options, such as the options group itself, a service or a route value, does not take that option's help line: the line keeps the option's own type and description.")]
	[DataRow("group", DisplayName = "The options-group parameter")]
	[DataRow("service", DisplayName = "A service parameter")]
	[DataRow("route", DisplayName = "A route parameter")]
	public void When_AParameterIsNamedLikeAGroupOption_Then_TheOptionKeepsItsHelpLine(string parameter)
	{
		var sut = ReplApp.Create();
		var command = parameter switch
		{
			"group" => "run",
			"service" => "run",
			"route" => "run {project:int}",
			_ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Unknown test row."),
		};
		_ = parameter switch
		{
			"group" => sut.Map(command, (ProjectOptions project) => project.Project ?? "none"),
			"service" => sut.Map(command, (ProjectOptions options, [FromServices] TimeProvider project) => options.Project ?? "none"),
			_ => sut.Map(command, (int project, ProjectOptions options) => options.Project ?? "none"),
		};

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["run", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		var line = output.Text.Split('\n').Single(candidate => candidate.Contains("--project"));
		line.Should().Contain("<string>").And.Contain("The app's project file, or its folder.");
		line.Should().NotContain("projectoptions").And.NotContain("timeprovider").And.NotContain("<int>");
	}

	[ReplOptionsGroup]
	public class ProjectOptions
	{
		[ReplOption(Name = "project", Mode = ReplParameterMode.OptionOnly)]
		[System.ComponentModel.Description("The app's project file, or its folder.")]
		public string? Project { get; set; }
	}

	[TestMethod]
	[Description("Verifies the default of a collection group property is rendered as its elements, in help and in the documentation model, and is absent when the collection is empty, never the collection's type name.")]
	public void When_ACollectionGroupPropertyHasADefault_Then_ItsElementsAreShown()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (CollectionDefaultsOptions options) => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));
		var options = sut.CreateDocumentationModel().Commands
			.Single(command => string.Equals(command.Path, "list", StringComparison.Ordinal)).Options;

		output.ExitCode.Should().Be(0);
		var lines = output.Text.Split('\n');
		lines.Single(line => line.Contains("--uids")).Should().NotContain("[default:");
		lines.Single(line => line.Contains("--tags")).Should().Contain("[default: a, b]");
		options.Single(option => string.Equals(option.Name, "uids", StringComparison.Ordinal)).DefaultValue.Should().BeNull();
		options.Single(option => string.Equals(option.Name, "tags", StringComparison.Ordinal)).DefaultValue.Should().Be("a, b");
	}

	[ReplOptionsGroup]
	public class CollectionDefaultsOptions
	{
		[ReplOption(Arity = ReplArity.ZeroOrMore)]
		public string[] Uids { get; set; } = [];

		[ReplOption(Arity = ReplArity.ZeroOrMore)]
		public string[] Tags { get; set; } = ["a", "b"];
	}

	[TestMethod]
	[Description("Regression guard: verifies a duplicate name between a group property and a regular parameter fails at registration with a duplicate-name diagnostic (not a token-collision one).")]
	public void When_GroupPropertyCollidesWithParam_Then_MapFails()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map("list", (TestOutputOptions output, string format) => format);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Duplicate parameter name*");
	}

	[TestMethod]
	[Description("Regression guard: verifies abstract options group type fails at registration.")]
	public void When_OptionsGroupTypeIsAbstract_Then_MapFails()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map("list", (AbstractGroup group) => "ok");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*concrete class*");
	}

	[TestMethod]
	[Description("Regression guard: verifies the same options group reused in two commands works.")]
	public void When_SameGroupReusedInTwoCommands_Then_BothWork()
	{
		var sut = ReplApp.Create();
		sut.Map("list", (TestOutputOptions output) => $"list:{output.Format}");
		sut.Map("show", (TestOutputOptions output) => $"show:{output.Format}");

		var listOutput = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["list", "--format", "json", "--no-logo"]));
		var showOutput = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["show", "--format", "xml", "--no-logo"]));

		listOutput.ExitCode.Should().Be(0);
		listOutput.Text.Should().Contain("list:json");
		showOutput.ExitCode.Should().Be(0);
		showOutput.Text.Should().Contain("show:xml");
	}

	[TestMethod]
	[Description("Regression guard: verifies positional binding on group properties is opt-in through ReplArgument.")]
	public void When_GroupPropertyUsesReplArgument_Then_PositionalBindingWorks()
	{
		var sut = ReplApp.Create();
		sut.Map("search", (PositionalSearchOptions options) => options.Query);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["search", "needle", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("needle");
	}

	[TestMethod]
	[Description("Regression guard: verifies group property cannot receive named and positional values in one invocation.")]
	public void When_GroupPropertyGetsNamedAndPositional_Then_InvocationFails()
	{
		var sut = ReplApp.Create();
		sut.Map("search", (PositionalSearchOptions options) => options.Query);

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["search", "--query", "alpha", "beta", "--no-logo"]));

		output.ExitCode.Should().Be(2);
		output.Text.Should().Contain("cannot receive both named and positional values");
	}

	[TestMethod]
	[Description("Regression guard: verifies positional group properties cannot be mixed with positional regular parameters.")]
	public void When_PositionalGroupPropertyMixedWithRegularPositional_Then_MapFails()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map(
			"search",
			(PositionalSearchOptions options, [ReplArgument] string term) => $"{options.Query}:{term}");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Cannot mix positional options-group properties*");
	}

	[ReplOptionsGroup]
	public abstract class AbstractGroup
	{
		public string Value { get; set; } = "";
	}
}
