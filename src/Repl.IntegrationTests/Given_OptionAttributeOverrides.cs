namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_OptionAttributeOverrides
{
	private enum ShadockSyllable
	{
		None = 0,

		[ReplEnumFlag("--ga", CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)]
		Ga = 1,

		[ReplEnumFlag("--bu")]
		Bu = 2,
	}

	[TestMethod]
	[Description("Regression guard for issue #57: [ReplOption(CaseSensitivity = ...)] must be a legal attribute argument (a nullable enum triggers CS0655, making the override dead code) and the per-option override must accept casing variants while the global default stays case-sensitive.")]
	public void When_OptionCaseSensitivityOverriddenViaAttribute_Then_CasingVariantIsAccepted()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)] string text) => text);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--Text", "bib overalls", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("bib overalls");
	}

	[TestMethod]
	[Description("Regression guard for issue #57 (expanded): [ReplOption(Arity = ...)] must be a legal attribute argument. A collection parameter naturally allows repetition (ZeroOrMore); the explicit ZeroOrOne override must be honored so a repeated option is rejected at parse time.")]
	public void When_ArityOverriddenViaAttributeToZeroOrOne_Then_RepeatedOptionIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.ZeroOrOne)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--items", "ga", "--items", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("accepts at most one value");
	}

	[TestMethod]
	[Description("Regression guard for issue #57 (expanded): [ReplValueAlias(..., CaseSensitivity = ...)] must be a legal attribute argument so an alias token matched ignoring case still injects its configured value.")]
	public void When_ValueAliasCaseSensitivityOverriddenViaAttribute_Then_CasingVariantInjectsValue()
	{
		var sut = ReplApp.Create();
		sut.Map("wear", ([ReplValueAlias("--denim", "bib overalls", CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)] string outfit = "none") => outfit);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wear", "--DENIM", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("bib overalls");
	}

	[TestMethod]
	[Description("Regression guard for issue #57 (expanded): [ReplEnumFlag(..., CaseSensitivity = ...)] must be a legal attribute argument so an enum-flag alias matched ignoring case binds the enum member while other members stay case-sensitive.")]
	public void When_EnumFlagCaseSensitivityOverriddenViaAttribute_Then_CasingVariantBindsEnumMember()
	{
		var sut = ReplApp.Create();
		sut.Map("say", (ShadockSyllable syllable = ShadockSyllable.None) => syllable.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["say", "--GA", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Ga");
	}

	[TestMethod]
	[Description("Guards the override boundary: a sibling enum-flag alias without a case-sensitivity override must keep the global case-sensitive default, so per-member overrides stay scoped to their own aliases.")]
	public void When_EnumFlagWithoutOverrideAndCasingDiffers_Then_TokenIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("say", (ShadockSyllable syllable = ShadockSyllable.None) => syllable.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["say", "--BU", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("--BU");
	}
}
