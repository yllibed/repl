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

		output.ExitCode.Should().Be(1);
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
