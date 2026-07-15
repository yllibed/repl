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

	[ReplOptionsGroup]
	public sealed class DenimOutfitOptions
	{
		[ReplOption(CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)]
		public string Fabric { get; set; } = "denim";

		[ReplOption(Arity = ReplArity.ZeroOrOne)]
		public string[] Patches { get; set; } = [];
	}

	public sealed class OverrideGlobals
	{
		[ReplOption(Name = "tenant", CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)]
		public string Tenant { get; set; } = "ga";
	}

	public sealed class OverrideArityGlobals
	{
		[ReplOption(Name = "retries", Arity = ReplArity.ExactlyOne)]
		public int Retries { get; set; } = 42;
	}

	public sealed class ValueAliasOverrideGlobals
	{
		[ReplValueAlias("--prod", "production", CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)]
		public string Environment { get; set; } = "dev";
	}

	[ReplOptionsGroup]
	public sealed class RequiredPatchesOptions
	{
		[ReplOption(Arity = ReplArity.OneOrMore)]
		public string[] Patches { get; set; } = [];
	}

	[ReplOptionsGroup]
	public sealed class SinglePatchOptions
	{
		[ReplOption(Mode = ReplParameterMode.OptionAndPositional, Arity = ReplArity.ZeroOrOne)]
		public string[] Patches { get; set; } = [];
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
	[Description("Guards the ambiguity edge unlocked by issue #57: a typed token matching both a case-insensitive alias and another option's case-sensitive canonical token must be rejected as ambiguous at parse time, never silently bound to either parameter.")]
	public void When_TokenMatchesCaseInsensitiveAliasAndAnotherOption_Then_AmbiguityIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"probe",
			([ReplOption(Aliases = ["--Mode"], CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)] string first = "ga",
			 [ReplOption(Name = "mode")] string second = "bu") => $"first={first};second={second}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["probe", "--mode", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Ambiguous option '--mode'");
	}

	[TestMethod]
	[Description("Guards the override boundary: a sibling enum-flag alias without a case-sensitivity override must keep the global case-sensitive default, so per-member overrides stay scoped to their own aliases.")]
	public void When_EnumFlagWithoutOverrideAndCasingDiffers_Then_TokenIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("say", (ShadockSyllable syllable = ShadockSyllable.None) => syllable.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["say", "--BU", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown option '--BU'");
	}

	[TestMethod]
	[Description("Guards the unset state of the Arity override: an attributed collection parameter without an explicit Arity must keep the inferred ZeroOrMore, so a builder-site drift back to the public (non-nullable) property — which compiles cleanly under ?. — would wrongly force ZeroOrOne and break repetition.")]
	public void When_AttributedCollectionParameterWithoutArityOverride_Then_RepetitionIsAccepted()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Name = "item")] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--item", "ga", "--item", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ga,bu");
	}

	[TestMethod]
	[Description("Guards the unset state of the CaseSensitivity override at execution: an attributed option without an explicit override must inherit the global CaseInsensitive default instead of the enum default (CaseSensitive, value 0).")]
	public void When_GlobalCaseInsensitiveAndNoOverride_Then_CasingVariantIsAccepted()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map("echo", ([ReplOption(Name = "channel")] string channel) => channel);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--Channel", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("zo");
	}

	[TestMethod]
	[Description("Guards the explicit-assignment-of-enum-value-zero edge: [ReplOption(CaseSensitivity = CaseSensitive)] (enum value 0) under a global CaseInsensitive default must register as a real override and reject casing variants — a sentinel-style refactor treating value 0 as unset would silently pass them.")]
	public void When_ExplicitCaseSensitiveOverrideUnderGlobalInsensitive_Then_CasingVariantIsRejected()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map("echo", ([ReplOption(CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string channel) => channel);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--Channel", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown option '--Channel'");
	}

	[TestMethod]
	[Description("Guards the options-group property path (a parallel branch in OptionSchemaBuilder): a case-sensitivity override on a group property must be honored the same way as on a handler parameter.")]
	public void When_GroupPropertyCaseSensitivityOverridden_Then_CasingVariantIsAccepted()
	{
		var sut = ReplApp.Create();
		sut.Map("wear", (DenimOutfitOptions options) => options.Fabric);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wear", "--FABRIC", "bib overalls", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("bib overalls");
	}

	[TestMethod]
	[Description("Guards the options-group property path for the arity override: a collection group property naturally infers ZeroOrMore; the explicit ZeroOrOne override must be honored so repetition is rejected.")]
	public void When_GroupPropertyArityOverriddenToZeroOrOne_Then_RepeatedOptionIsRejected()
	{
		var sut = ReplApp.Create();
		sut.Map("wear", (DenimOutfitOptions options) => string.Join(',', options.Patches));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wear", "--patches", "ga", "--patches", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("accepts at most one value");
	}

	[TestMethod]
	[Description("Guards execution parity for enum duplicate detection: with an explicit ZeroOrMore arity (reachable only through the now-settable override), repeated enum values differing only by casing are distinct under the global CaseSensitive default and must be reported as conflicting — the validator previously hardcoded case-insensitive comparison when no per-option override was set.")]
	public void When_RepeatedEnumValuesDifferByCaseUnderGlobalCaseSensitive_Then_ConflictIsReported()
	{
		var sut = ReplApp.Create();
		sut.Map("say", ([ReplOption(Arity = ReplArity.ZeroOrMore)] ShadockSyllable syllable = ShadockSyllable.None) => syllable.ToString());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["say", "--syllable", "Ga", "--syllable", "GA", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("received multiple enum values");
	}

	[TestMethod]
	[Description("Guards against a silent no-op newly reachable through issue #57: typed global options (UseGlobalOptions<T>) do not support per-option CaseSensitivity/Arity overrides, so declaring one must fail fast at registration instead of being silently discarded.")]
	public void When_GlobalOptionsPropertyDeclaresOverride_Then_RegistrationFailsFast()
	{
		var act = () => ReplApp.Create().UseGlobalOptions<OverrideGlobals>();

		act.Should().Throw<NotSupportedException>().WithMessage("*CaseSensitivity*");
	}

	[TestMethod]
	[Description("Guards collision validation against false positives newly reachable through issue #57: under a global CaseInsensitive default, two options explicitly marked CaseSensitive whose tokens differ only by casing are distinguishable ordinally at resolution time, so registration must accept them and route each token to its own parameter.")]
	public void When_TwoExplicitlyCaseSensitiveOptionsDifferOnlyByCase_Then_RegistrationAndResolutionSucceed()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map(
			"cfg",
			([ReplOption(Name = "mode", CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string lower = "ga",
			 [ReplOption(Name = "MODE", CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string upper = "bu") => $"lower={lower};upper={upper}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["cfg", "--MODE", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("lower=ga;upper=zo");
	}

	[TestMethod]
	[Description("Guards the OneOrMore lower bound newly reachable through issue #57: an option with an explicit OneOrMore arity invoked without any value (named or positional) must fail with an arity diagnostic instead of invoking the handler with a missing value.")]
	public void When_OneOrMoreArityOptionIsAbsent_Then_BindingFailsWithoutInvokingHandler()
	{
		var invoked = false;
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.OneOrMore)] string[] items) =>
		{
			invoked = true;
			return string.Join(',', items);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("requires at least one value");
		invoked.Should().BeFalse();
	}

	[TestMethod]
	[Description("Guards the boundary of the OneOrMore lower bound: values consumed positionally satisfy the arity, so the absence check must run only after both named and positional binding attempts — not at option-parse time, which cannot see positional consumption.")]
	public void When_OneOrMoreArityOptionReceivesPositionalValues_Then_BindingSucceeds()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.OneOrMore)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "ga", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ga,bu");
	}

	[TestMethod]
	[Description("Guards the OneOrMore lower bound on the options-group property path: an absent group property with an explicit OneOrMore arity must fail binding instead of silently keeping its default and invoking the handler.")]
	public void When_GroupPropertyOneOrMoreArityIsAbsent_Then_BindingFailsWithoutInvokingHandler()
	{
		var invoked = false;
		var sut = ReplApp.Create();
		sut.Map("wear", (RequiredPatchesOptions options) =>
		{
			invoked = true;
			return string.Join(',', options.Patches);
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wear", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("requires at least one value");
		invoked.Should().BeFalse();
	}

	[TestMethod]
	[Description("Guards the OneOrMore lower bound for ArgumentOnly parameters: they have no named-option schema entry, so the explicit arity must be carried at the parameter level — otherwise the declared override is silently dropped and the handler receives a missing value.")]
	public void When_ArgumentOnlyOneOrMoreArityIsAbsent_Then_BindingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("copy", ([ReplOption(Mode = ReplParameterMode.ArgumentOnly, Arity = ReplArity.OneOrMore)] string[] files) => string.Join(',', files));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["copy", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("requires at least one value");
	}

	[TestMethod]
	[Description("Boundary for the ArgumentOnly OneOrMore bound: positional values satisfy the explicit arity, so the same registration invoked with positionals must bind and execute.")]
	public void When_ArgumentOnlyOneOrMoreArityReceivesPositionals_Then_BindingSucceeds()
	{
		var sut = ReplApp.Create();
		sut.Map("copy", ([ReplOption(Mode = ReplParameterMode.ArgumentOnly, Arity = ReplArity.OneOrMore)] string[] files) => string.Join(',', files));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["copy", "ga", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ga,bu");
	}

	[TestMethod]
	[Description("Guards the explicit ExactlyOne lower bound: ReplArity.ExactlyOne is documented as 'must appear exactly one time', so an EXPLICIT override on an otherwise-optional parameter must reject absence — while inferred ExactlyOne (plain required scalars) keeps its existing binding behavior.")]
	public void When_ExplicitExactlyOneArityOptionIsAbsent_Then_BindingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.ExactlyOne)] string? channel = null) => channel ?? "none");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("requires exactly one value");
	}

	[TestMethod]
	[Description("Guards diagnostic accuracy: the lower-bound failure message must name the canonical option token ([ReplOption(Name = ...)]), not the CLR parameter name — telling the user to supply a token the parser would reject is actively misleading.")]
	public void When_RenamedOneOrMoreOptionIsAbsent_Then_MessageUsesCanonicalToken()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Name = "item", Arity = ReplArity.OneOrMore)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Option '--item' requires");
	}

	[TestMethod]
	[Description("Guards the Arity branch of the typed-global-options fail-fast: reverting only that branch would silently discard the override while the CaseSensitivity twin keeps the suite green.")]
	public void When_GlobalOptionsPropertyDeclaresArityOverride_Then_RegistrationFailsFast()
	{
		var act = () => ReplApp.Create().UseGlobalOptions<OverrideArityGlobals>();

		act.Should().Throw<NotSupportedException>().WithMessage("*Arity*");
	}

	[TestMethod]
	[Description("Guards the value-alias branch of the typed-global-options fail-fast: a [ReplValueAlias(..., CaseSensitivity = ...)] override on a global property is equally newly settable and must be rejected instead of silently discarded.")]
	public void When_GlobalOptionsValueAliasDeclaresOverride_Then_RegistrationFailsFast()
	{
		var act = () => ReplApp.Create().UseGlobalOptions<ValueAliasOverrideGlobals>();

		act.Should().Throw<NotSupportedException>().WithMessage("*CaseSensitivity*");
	}

	[TestMethod]
	[Description("Guards suggestion fidelity for case-distinct tokens: KnownTokens must not dedupe tokens ignoring case, otherwise one of two case-sensitive twins vanishes and 'Did you mean' proposes the wrong casing.")]
	public void When_CaseSensitiveTwinTokensRegistered_Then_SuggestionPreservesEachCasing()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"cfg",
			([ReplOption(Name = "mode")] string lower = "ga",
			 [ReplOption(Name = "MODE")] string upper = "bu") => $"lower={lower};upper={upper}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["cfg", "--MODEs", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Did you mean '--MODE'");
	}

	[TestMethod]
	[Description("Guards diagnostic context: a token collision thrown at Map() time must name the command being registered, so an app with many registrations does not need a debugger to find the offending one.")]
	public void When_TokenCollisionDetected_Then_MessageNamesTheCommand()
	{
		var sut = ReplApp.Create();
		var act = () => sut.Map(
			"cfg",
			([ReplOption(Name = "mode")] string first = "ga",
			 [ReplOption(Aliases = ["--mode"])] string second = "bu") => $"{first}{second}");

		act.Should().Throw<InvalidOperationException>().WithMessage("*cfg*");
	}

	[TestMethod]
	[Description("Guards diagnostic wording: a group property whose name collides with a handler parameter is a duplicate-name error, not a token collision — the old wording sent developers hunting for alias conflicts that do not exist.")]
	public void When_GroupPropertyNameCollidesWithParameter_Then_MessageSaysDuplicateName()
	{
		var sut = ReplApp.Create();
		var act = () => sut.Map(
			"wear",
			(DenimOutfitOptions options, [ReplOption] string fabric = "denim") => fabric);

		act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
	}

	[TestMethod]
	[Description("Guards named/positional parity for explicit upper bounds: a ZeroOrOne collection rejects a repeated named option, so two positional values feeding the same collection must be rejected too — otherwise the override is enforceable on one input path and bypassable on the other.")]
	public void When_ZeroOrOneCollectionReceivesTwoPositionals_Then_BindingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.ZeroOrOne)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "ga", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("accepts at most one value");
	}

	[TestMethod]
	[Description("Guards named/positional parity for the ExactlyOne upper bound on the positional path: two positional values must be rejected with the exactly-one diagnostic, mirroring the named-option validator.")]
	public void When_ExactlyOneCollectionReceivesTwoPositionals_Then_BindingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.ExactlyOne)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "ga", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("requires exactly one value");
	}

	[TestMethod]
	[Description("Guards named/positional parity for explicit upper bounds on the options-group property path: a positional ZeroOrOne collection property must reject two positional values the same way repeated named values are rejected.")]
	public void When_GroupZeroOrOneCollectionReceivesTwoPositionals_Then_BindingFails()
	{
		var sut = ReplApp.Create();
		sut.Map("wear", (SinglePatchOptions options) => string.Join(',', options.Patches));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wear", "ga", "bu", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("accepts at most one value");
	}

	[TestMethod]
	[Description("Guards the CaseSensitive override against the permissive unknown-option fallback: with AllowUnknownOptions and a global CaseInsensitive default, a casing-mismatched token must stay an inert unknown instead of rebinding by parameter name to an option that explicitly rejected that casing.")]
	public void When_PermissiveUnknownTokenCaseMismatchesCaseSensitiveOption_Then_ValueIsNotBound()
	{
		var sut = ReplApp.Create()
			.Options(options =>
			{
				options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
				options.Parsing.AllowUnknownOptions = true;
			});
		sut.Map("run", ([ReplOption(CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string mode = "ga") => mode);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["run", "--Mode", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ga");
		output.Text.Should().NotContain("zo");
	}

	[TestMethod]
	[Description("Boundary for the permissive-fallback guard: without a per-option override, permissive mode keeps binding unknown casing variants by parameter name under the global CaseInsensitive comparer — the documented migration behavior must survive the fix.")]
	public void When_PermissiveUnknownTokenCaseMismatchesUnconstrainedOption_Then_ValueStillBinds()
	{
		var sut = ReplApp.Create()
			.Options(options =>
			{
				options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
				options.Parsing.AllowUnknownOptions = true;
			});
		sut.Map("run", ([ReplOption] string mode = "ga") => mode);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["run", "--MoDe", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("zo");
	}

	[TestMethod]
	[Description("Guards the permissive-fallback guard against the renamed-token edge: when [ReplOption(Name = ...)] differs from the CLR parameter name only by casing, a token rejected by the case-sensitive schema entry must not slip through the name-equality path of the fallback and bind anyway.")]
	public void When_PermissiveTokenMatchesRenamedCaseSensitiveOption_Then_ValueIsNotBound()
	{
		var sut = ReplApp.Create()
			.Options(options =>
			{
				options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
				options.Parsing.AllowUnknownOptions = true;
			});
		sut.Map("run", ([ReplOption(Name = "Mode", CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string mode = "ga") => mode);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["run", "--mode", "zo", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ga");
		output.Text.Should().NotContain("zo");
	}

	[TestMethod]
	[Description("Guards documentation fidelity for explicit lower bounds: an option that now fails when omitted (explicit OneOrMore) must be exported as required — otherwise generated docs tell users a runtime-required option is optional.")]
	public void When_ExportingDocForExplicitOneOrMoreOption_Then_OptionIsRequired()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("echo", ([ReplOption(Arity = ReplArity.OneOrMore)] string[] items) => string.Join(',', items));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["doc", "export", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"required\": true");
	}

	[TestMethod]
	[Description("Guards documentation fidelity on the options-group path: group options were hard-coded as not required, so an explicit OneOrMore group property — which now fails binding when omitted — must be exported as required.")]
	public void When_ExportingDocForExplicitOneOrMoreGroupProperty_Then_OptionIsRequired()
	{
		var sut = ReplApp.Create()
			.UseDocumentationExport();
		sut.Map("wear", (RequiredPatchesOptions options) => string.Join(',', options.Patches));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["doc", "export", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"required\": true");
	}
}
