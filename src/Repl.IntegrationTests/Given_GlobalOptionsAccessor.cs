using Microsoft.Extensions.DependencyInjection;
using Repl.Parameters;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_GlobalOptionsAccessor
{
	[TestMethod]
	[Description("Global option is accessible in handler via IGlobalOptionsAccessor parameter.")]
	public void When_GlobalOptionProvided_Then_HandlerCanReadItViaAccessor()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Help renders one row per global option and lists that option's aliases, but GlobalOptionParser gives a colliding token to the LAST registration. A token listed as a visible option's alias can therefore bind a hidden option, so help must decide visibility per token rather than per definition — the same mismatch the completion source had.")]
	public void When_AVisibleGlobalAliasCollidesWithALaterHiddenOption_Then_HelpDoesNotAdvertiseTheToken()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("region", aliases: ["--tenant", "-r"]);
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map("show", () => "ok");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--region", "the visible option keeps its own row");
		help.Text.Should().Contain("-r", "an alias with no collision stays listed");
		help.Text.Should().NotContain("--tenant", "accepting this token would bind the hidden option");
	}

	[TestMethod]
	[Description("The reverse collision: a later hidden definition claims a visible option's CANONICAL token while leaving its aliases alone. The alias is still accepted by the parser and offered by completion, so dropping the whole row would hide a reachable option. Rows are therefore built from whichever tokens the definition still owns, with the canonical one carrying no special weight.")]
	public void When_AHiddenGlobalClaimsAVisibleCanonicalToken_Then_HelpStillListsTheSurvivingAlias()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("region", aliases: ["-r"]);
			options.Parsing.AddGlobalOption<string>("zone", aliases: ["--region"]);
			options.Parsing.GlobalOption("zone").Hidden();
		});
		sut.Map("show", () => "ok");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("-r", "the alias nobody claimed keeps the option discoverable");
		help.Text.Should().NotContain("--region", "that token now binds the hidden option");
		help.Text.Should().NotContain("--zone");
	}

	[TestMethod]
	[Description("A hidden definition registered first must not contribute a token that a later visible definition owns. Otherwise root help renders the token twice and leaks the hidden definition's description under the duplicate row.")]
	public void When_AVisibleGlobalClaimsAHiddenAlias_Then_HelpRendersOnlyTheVisibleOwner()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>(
				"legacy",
				aliases: ["--tenant"],
				defaultValue: null,
				description: "Hidden legacy tenant.");
			options.Parsing.AddGlobalOption<string>(
				"tenant",
				aliases: null,
				defaultValue: null,
				description: "Visible tenant.");
			options.Parsing.GlobalOption("legacy").Hidden();
		});
		sut.Map("show", () => "ok");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var tenantRows = help.Text
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
			.Where(static line => line.Contains("--tenant", StringComparison.Ordinal))
			.ToArray();

		help.ExitCode.Should().Be(0);
		tenantRows.Should().ContainSingle();
		tenantRows[0].Should().Contain("Visible tenant.");
		help.Text.Should().NotContain("Hidden legacy tenant.");
	}

	[TestMethod]
	[Description("A manually registered hidden global option is omitted from help — token, alias, description and default alike — while staying bindable through the accessor. A visible sibling whose own token contains the hidden one's alias is registered on purpose: asserting the bare substring \"-t\" would pass only for as long as no other rendered token happened to contain it, so the assertions target the rendered option row instead.")]
	public void When_ManualGlobalOptionIsHidden_Then_HelpOmitsItsRowAndExplicitInvocationStillBinds()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption(
				"tenant",
				aliases: ["-t"],
				defaultValue: "internal-default",
				description: "Internal tenant selector.");
			options.Parsing.AddGlobalOption<string>(
				"denim-tint",
				aliases: ["-d"],
				defaultValue: null,
				description: "Visible control.");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().NotContain("--tenant");
		help.Text.Should().NotContain("--tenant, -t");
		help.Text.Should().NotContain("Internal tenant selector.");
		help.Text.Should().NotContain("internal-default");
		help.Text.Should().Contain("--denim-tint, -d", "the visible sibling proves the whole option table was not simply empty");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global option is accessible in middleware via DI.")]
	public void When_GlobalOptionProvided_Then_MiddlewareCanReadIt()
	{
		string? captured = null;
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Use(async (ctx, next) =>
		{
			var globals = ctx.Services.GetRequiredService<IGlobalOptionsAccessor>();
			captured = globals.GetValue<string>("tenant");
			await next().ConfigureAwait(false);
		});
		sut.Map("ping", () => "pong");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["ping", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		captured.Should().Be("acme");
	}

	[TestMethod]
	[Description("Global option is accessible in DI factory via lazy resolution.")]
	public void When_GlobalOptionProvided_Then_DiFactoryCanReadIt()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton(sp =>
			{
				var globals = sp.GetRequiredService<IGlobalOptionsAccessor>();
				return new TenantConfig(globals.GetValue<string>("tenant") ?? "default");
			});
		});
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (TenantConfig cfg) => cfg.Name);

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global option defaults are returned when option not provided.")]
	public void When_GlobalOptionNotProvided_Then_DefaultIsReturned()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<int>("port", defaultValue: 3000));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<int>("port"));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("3000");
	}

	[TestMethod]
	[Description("Regression guard: for typed global options, the accessor mirrors the prototype default even when it equals the CLR default (int = 0), staying consistent with the injected instance which always carries prototype values.")]
	public void When_TypedGlobalOptionHasClrDefaultPrototypeValue_Then_AccessorMatchesInjectedInstance()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<ClrDefaultGlobals>();
		sut.Map("show", (IGlobalOptionsAccessor globals, ClrDefaultGlobals opts) =>
			$"accessor:{globals.GetValue<int>("retries", 42)} injected:{opts.Retries}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("accessor:0 injected:0");
	}

	[TestMethod]
	[Description("Regression guard: an implicit CLR default for a value type outside the primitive whitelist (Guid.Empty) is not stored as registration metadata, so the call-site fallback wins when the option is omitted.")]
	public void When_GlobalOptionDeclaresImplicitGuidDefault_Then_CallSiteFallbackWins()
	{
		var fallback = new Guid(0x42424242, 0x4242, 0x4242, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42);
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<Guid>("session", aliases: null, defaultValue: default, description: "Session id."));
		sut.Map("show", (IGlobalOptionsAccessor globals) => $"session:{globals.GetValue<Guid>("session", fallback)}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain($"session:{fallback}");
	}

	[TestMethod]
	[Description("Regression guard: an explicit registration default equal to the CLR default of the underlying type (0), declared through a nullable type parameter, is preserved as metadata and applied when the option is omitted instead of the call-site fallback.")]
	public void When_NullableGlobalOptionDeclaresUnderlyingClrDefault_Then_RegisteredDefaultWins()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<int?>("port", defaultValue: 0));
		sut.Map("show", (IGlobalOptionsAccessor globals) => $"port:{globals.GetValue<int>("port", 8080)}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("port:0");
	}

	[TestMethod]
	[Description("Creating documentation must inspect configured services without freezing the shared provider. A typed global-options extension registered afterward must still be visible to the provider that Run reuses.")]
	public void When_DocumentationPrecedesTypedGlobalRegistration_Then_RunUsesTheLateServiceRegistration()
	{
		var sut = ReplApp.Create();
		_ = sut.CreateDocumentationModel();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions options) => options.Tenant ?? "none");

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0, output.Text);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> registers typed class accessible via DI.")]
	public void When_UsingTypedGlobalOptions_Then_ClassIsPopulatedFromParsedValues()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant}:{opts.Port}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--port", "9090", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme:9090");
	}

	[TestMethod]
	[Description("ReplOption.Hidden on a typed global-options property hides discovery while preserving typed injection and binding.")]
	public void When_TypedGlobalOptionPropertyIsHidden_Then_HelpOmitsItAndExplicitInvocationStillBinds()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<HiddenGlobalOptions>();
		sut.Map("show", (HiddenGlobalOptions options) => $"{options.Region}:{options.InternalToken}");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(
			["show", "--region", "east", "--internal-token", "secret", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--region");
		help.Text.Should().NotContain("--internal-token");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("east:secret");
	}

	[TestMethod]
	[Description("ReplOption.HiddenAliases on a typed global property preserves legacy parsing while root help keeps the canonical token and omits the old spelling.")]
	public void When_TypedGlobalOptionHasHiddenAlias_Then_HelpOmitsOnlyTheAliasAndBindingRetainsIt()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<LegacyGlobalOptions>();
		sut.Map("show", static string (LegacyGlobalOptions options) => options.Tenant ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["--account", "acme", "show", "--no-logo"]));

		help.Text.Should().Contain("--tenant");
		help.Text.Should().NotContain("--account");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("In case-sensitive mode, hiding one global alias spelling leaves a differently-cased alias visible and both spellings remain parsable.")]
	public void When_GlobalAliasesDifferOnlyByCase_Then_HidingOnePreservesTheOther()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive;
			options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--account", "--ACCOUNT"]);
			options.Parsing.GlobalOption("tenant").HiddenAlias("--account");
		});
		sut.Map("show", static string () => "ok");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var hiddenInvocation = ConsoleCaptureHelper.Capture(() => sut.Run(["--account", "acme", "show", "--no-logo"]));
		var visibleInvocation = ConsoleCaptureHelper.Capture(() => sut.Run(["--ACCOUNT", "acme", "show", "--no-logo"]));

		help.Text.Should().Contain("--ACCOUNT");
		help.Text.Should().NotContain("--account");
		hiddenInvocation.ExitCode.Should().Be(0, hiddenInvocation.Text);
		visibleInvocation.ExitCode.Should().Be(0, visibleInvocation.Text);
	}

	[TestMethod]
	[Description("HiddenAlias prefers an exact registered token before the active case-insensitive fallback, so changing comparison modes cannot hide the wrong case-distinct alias when case sensitivity is restored.")]
	public void When_CaseModeChangesBeforeHidingAnExactGlobalAlias_Then_TheExactAliasIsRetained()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive;
			options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--account", "--ACCOUNT"]);
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			options.Parsing.GlobalOption("tenant").HiddenAlias("--ACCOUNT");
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive;
		});
		sut.Map("show", static string () => "ok");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		help.Text.Should().Contain("--account");
		help.Text.Should().NotContain("--ACCOUNT");
	}

	[TestMethod]
	[Description("A fluent Hidden(false) override re-exposes a typed global option hidden by attribute.")]
	public void When_TypedGlobalOptionHiddenAttributeIsOverriddenWithFalse_Then_RootHelpListsIt()
	{
		var sut = ReplApp.Create()
			.UseGlobalOptions<HiddenGlobalOptions>()
			.Options(options => options.Parsing.GlobalOption("internal-token").Hidden(isHidden: false));
		sut.Map("show", (HiddenGlobalOptions globals) => globals.InternalToken ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--internal-token");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> handler parameters are injected from DI even when the parameter name matches a global option.")]
	public void When_TypedGlobalOptionsParameterNameMatchesGlobalOption_Then_HandlerReceivesDiInstance()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions tenant) => tenant.Tenant ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0, output.Text);
		output.Text.Should().Contain("acme");
		output.Text.Should().NotContain("Ambiguous option");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> supports handler parameters typed as an implemented options interface.")]
	public void When_TypedGlobalOptionsParameterUsesImplementedInterface_Then_HandlerReceivesDiInstance()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<InterfaceGlobalOptions>();
		sut.Map("show", (IInterfaceGlobalOptions tenant) => tenant.Tenant ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0, output.Text);
		output.Text.Should().Contain("acme");
		output.Text.Should().NotContain("Ambiguous option");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> rejects command binding attributes on typed global-options parameters.")]
	public void When_TypedGlobalOptionsParameterDeclaresReplOption_Then_MappingFailsClearly()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();

		var act = () => sut.Map(
			"show",
			([ReplOption(Name = "tenant")] TestGlobalOptions options) => options.Tenant ?? "none");

		var exception = act.Should().Throw<InvalidOperationException>().Which;
		exception.Message.Should().Contain("UseGlobalOptions");
		exception.Message.Should().Contain(nameof(TestGlobalOptions));
		exception.Message.Should().Contain("ReplOption");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> rejects positional binding attributes on typed global-options parameters.")]
	public void When_TypedGlobalOptionsParameterDeclaresReplArgument_Then_MappingFailsClearly()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();

		var act = () => sut.Map(
			"show",
			([ReplArgument] TestGlobalOptions options) => options.Tenant ?? "none");

		var exception = act.Should().Throw<InvalidOperationException>().Which;
		exception.Message.Should().Contain("UseGlobalOptions");
		exception.Message.Should().Contain(nameof(TestGlobalOptions));
		exception.Message.Should().Contain("ReplArgument");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> reports typed-options DI registration issues with actionable diagnostics.")]
	public void When_TypedGlobalOptionsServiceIsMissing_Then_RuntimeErrorMentionsUseGlobalOptions()
	{
		var sut = CoreReplApp.Create();
		sut.RegisterGlobalOptionsType(typeof(MissingServiceGlobalOptions));
		sut.Map("show", (MissingServiceGlobalOptions options) => options.Tenant ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(2);
		output.Text.Should().Contain("UseGlobalOptions");
		output.Text.Should().Contain(nameof(MissingServiceGlobalOptions));
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> duplicate property names report the typed options classes involved.")]
	public void When_TypedGlobalOptionsPropertyNamesCollide_Then_ErrorMentionsBothTypes()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();

		var act = () => sut.UseGlobalOptions<DuplicateTenantGlobalOptions>();

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*tenant*TestGlobalOptions*DuplicateTenantGlobalOptions*");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> properties without values keep defaults.")]
	public void When_UsingTypedGlobalOptionsWithoutValues_Then_DefaultsAreKept()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant ?? "none"}:{opts.Port}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("none:8080");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> property defaults are visible via IGlobalOptionsAccessor.")]
	public void When_UsingTypedGlobalOptionsWithoutValues_Then_AccessorReturnsPropertyDefaults()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<int>("port"));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("8080");
	}

	[TestMethod]
	[Description("Global option registered with string type name works end to end.")]
	public void When_GlobalOptionRegisteredWithStringTypeName_Then_TypedAccessWorks()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption("port", "int"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<int>("port"));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--port", "4000", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("4000");
	}

	[TestMethod]
	[Description("CoreReplApp (no MS DI) also provides IGlobalOptionsAccessor.")]
	public void When_UsingCoreReplApp_Then_AccessorIsAvailableInHandler()
	{
		var sut = CoreReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> returns fresh values on each resolution (not stale singleton).")]
	public void When_TypedOptionsResolvedMultipleTimes_Then_ReflectsLatestValues()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		var results = new List<string>();
		sut.Map("show", (TestGlobalOptions opts) =>
		{
			results.Add($"{opts.Tenant}:{opts.Port}");
			return "ok";
		});

		// First invocation
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "first", "--port", "1111", "--no-logo"]));

		// Second invocation with different values
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "second", "--port", "2222", "--no-logo"]));

		results.Should().HaveCount(2);
		results[0].Should().Be("first:1111");
		results[1].Should().Be("second:2222");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> uses configured NumericFormatProvider, not invariant culture.")]
	public void When_NumericCultureIsCurrent_Then_TypedOptionsUsesConfiguredCulture()
	{
		var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
		try
		{
			// Set a culture that uses comma as decimal separator
			System.Globalization.CultureInfo.CurrentCulture =
				new System.Globalization.CultureInfo("fr-FR");

			var sut = ReplApp.Create();
			sut.Options(o => o.Parsing.NumericCulture = NumericParsingCulture.Current);
			sut.UseGlobalOptions<DecimalGlobalOptions>();
			sut.Map("show", (DecimalGlobalOptions opts) => opts.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture));

			// fr-FR uses comma, but we pass "1,5" which should parse with current culture
			var output = ConsoleCaptureHelper.Capture(
				() => sut.Run(["show", "--rate", "1,5", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("1.5");
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = previousCulture;
		}
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> converts consecutive uppercase property names to kebab-case correctly.")]
	public void When_PropertyHasConsecutiveUppercase_Then_KebabCaseIsCorrect()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<AcronymGlobalOptions>();
		sut.Map("show", (AcronymGlobalOptions opts) => $"{opts.XMLPort}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--xml-port", "9090", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("9090");
	}

	[TestMethod]
	[Description("Sub-invocation via RunSubInvocationAsync preserves baseline from top-level Run.")]
	public async Task When_SubInvocationAfterRun_Then_BaselineGlobalOptionsArePreserved()
	{
		string? capturedTenant = null;
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant}:{opts.Port}");
		sut.Map("check", (IGlobalOptionsAccessor globals) =>
		{
			capturedTenant = globals.GetValue<string>("tenant");
			return "ok";
		});

		// Top-level Run establishes baseline with --tenant acme.
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		// Sub-invocation without --tenant must still see the baseline value.
		await sut.Core.RunSubInvocationAsync(
			["--no-logo", "check"], sut.Services).ConfigureAwait(false);

		capturedTenant.Should().Be("acme");
	}

	[TestMethod]
	[Description("Regression guard: a sub-invocation preserved the baseline VALUE but dropped its explicitness. Update merges parsed values over the session baseline, then replaced the explicit-key set with the sub-invocation's own keys — so HasValue denied an option whose value GetValue still returned. A module presence predicate reading HasValue then decided differently depending on whether a top-level run or a sub-invocation went last, which is how an MCP catalog advertises a tool its own execution rejects as unknown.")]
	public async Task When_SubInvocationAfterRun_Then_BaselineGlobalOptionsAreStillExplicit()
	{
		bool? capturedHasTenant = null;
		string? capturedTenant = null;
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant}");
		sut.Map("check", (IGlobalOptionsAccessor globals) =>
		{
			capturedHasTenant = globals.HasValue("tenant");
			capturedTenant = globals.GetValue<string>("tenant");
			return "ok";
		});

		// Top-level Run establishes the baseline with --tenant acme.
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		await sut.Core.RunSubInvocationAsync(
			["--no-logo", "check"], sut.Services).ConfigureAwait(false);

		capturedTenant.Should().Be("acme");
		capturedHasTenant.Should().BeTrue(
			because: "the value is still in effect, so denying it was provided contradicts GetValue");
	}

	[TestMethod]
	[Description("Sub-invocation does not reset baseline for subsequent sub-invocations.")]
	public async Task When_MultipleSubInvocations_Then_BaselineRemainsStable()
	{
		var captured = new List<string?>();
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => "ok");
		sut.Map("check", (IGlobalOptionsAccessor globals) =>
		{
			captured.Add(globals.GetValue<string>("tenant"));
			return "ok";
		});

		// Top-level Run establishes baseline.
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		// Two consecutive sub-invocations — baseline must survive both.
		await sut.Core.RunSubInvocationAsync(
			["--no-logo", "check"], sut.Services).ConfigureAwait(false);
		await sut.Core.RunSubInvocationAsync(
			["--no-logo", "check"], sut.Services).ConfigureAwait(false);

		captured.Should().AllBe("acme");
	}

	[TestMethod]
	[Description("Regression for the interactive committed-input order: parsed globals are applied BEFORE route resolution, so once the routing cache is invalidated, a module gated on a per-command global (--env prod) is present for that same command line. Red was observed with the Update call moved after route resolution.")]
	public async Task When_GlobalGatedModuleResolvesInteractively_Then_PerCommandGlobalIsVisibleToPresencePredicate()
	{
		var output = new StringWriter();
		var sut = ReplApp.Create();
		// Autocomplete resolves the routing graph per keystroke; keep it out of the way so
		// the committed-input resolution is the first one after the invalidation below.
		sut.Options(options =>
		{
			options.Interactive.Autocomplete.Mode = AutocompleteMode.Off;
			options.Parsing.AddGlobalOption<string>("env");
		});
		sut.MapModule(
			new EnvGatedModule(),
			context => string.Equals(
				(context.ServiceProvider.GetService(typeof(IGlobalOptionsAccessor)) as IGlobalOptionsAccessor)
					?.GetValue<string>("env"),
				"prod",
				StringComparison.Ordinal));
		sut.Map("reload", () =>
		{
			sut.Core.InvalidateRouting();
			return "reloaded";
		});
		var host = new StreamedReplHost(output, new StaticWindowSizeProvider());

		host.EnqueueInput($"reload{Environment.NewLine}secret --env prod{Environment.NewLine}exit{Environment.NewLine}");
		var exitCode = await host.RunSessionAsync(sut, new ReplRunOptions());

		exitCode.Should().Be(0);
		output.ToString().Should().Contain(
			"classified-42",
			because: "the per-command global must be applied before the re-evaluated routing graph gates the module");
	}

	private sealed class EnvGatedModule : IReplModule
	{
		public void Map(IReplMap map) => map.Map("secret", () => "classified-42");
	}

	private sealed class DecimalGlobalOptions
	{
		public double Rate { get; set; }
	}

	private sealed class AcronymGlobalOptions
	{
		public int XMLPort { get; set; }
	}

	private sealed record TenantConfig(string Name);

	private sealed class TestGlobalOptions
	{
		public string? Tenant { get; set; }

		public int Port { get; set; } = 8080;
	}

	private sealed class ClrDefaultGlobals
	{
		public int Retries { get; set; }
	}

	private sealed class LegacyGlobalOptions
	{
		[ReplOption(HiddenAliases = ["--account"])]
		public string? Tenant { get; set; }
	}

	private sealed class HiddenGlobalOptions
	{
		public string? Region { get; set; }

		[ReplOption(Hidden = true)]
		public string? InternalToken { get; set; }
	}

	private interface IInterfaceGlobalOptions
	{
		string? Tenant { get; }
	}

	private sealed class InterfaceGlobalOptions : IInterfaceGlobalOptions
	{
		public string? Tenant { get; set; }
	}

	private sealed class MissingServiceGlobalOptions
	{
		public string? Tenant { get; set; }
	}

	private sealed class DuplicateTenantGlobalOptions
	{
		public string? Tenant { get; set; }
	}
}
