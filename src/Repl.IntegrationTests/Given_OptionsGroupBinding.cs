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
	public class TestPagingOptions
	{
		[ReplOption]
		public int Limit { get; set; } = 10;

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
	[Description("Regression guard: verifies token collision between group and regular parameter fails at registration.")]
	public void When_GroupPropertyCollidesWithParam_Then_MapFails()
	{
		var sut = ReplApp.Create();

		var act = () => sut.Map("list", (TestOutputOptions output, string format) => format);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*collision*");
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

	[ReplOptionsGroup]
	public abstract class AbstractGroup
	{
		public string Value { get; set; } = "";
	}
}
