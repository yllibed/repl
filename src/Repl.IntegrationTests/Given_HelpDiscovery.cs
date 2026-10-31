using ComponentDescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Microsoft.Extensions.DependencyInjection;
using Repl.Spectre;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HelpDiscovery
{
	private static readonly string[] SingleResult = ["one"];

	[TestMethod]
	[Description("Regression guard: verifies requesting root help so that hidden commands are excluded.")]
	public void When_RequestingRootHelp_Then_HiddenCommandsAreExcluded()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");
		sut.Map("debug dump-state", () => "ok").Hidden();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("contact");
		output.Text.Should().NotContain("debug");
		output.Text.Should().Contain("Global Commands:");
		output.Text.Should().Contain("help [path]");
		output.Text.Should().Contain("human, json, yaml, xml, markdown");
		output.Text.Should().NotContain("markdown, spectre");
		output.Text.Should().NotContain("? [path]");
		output.Text.Should().NotContain("history [--limit <n>]");
		output.Text.Should().NotContain("complete --target <name>");
		output.Text.Should().Contain("exit");
	}

	[TestMethod]
	[Description("Regression guard: verifies hidden context is excluded from root discovery while unrelated commands remain visible.")]
	public void When_RequestingRootHelp_Then_HiddenContextsAreExcluded()
	{
		var sut = ReplApp.Create();
		sut.Map("status", () => "ok").WithDescription("Show status");
		sut.Context("admin", admin =>
		{
			admin.Map("reset", () => "done").WithDescription("Reset state");
		}).Hidden();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("status");
		output.Text.Should().NotContain("admin");
		output.Text.Should().NotContain("reset");
	}

	[TestMethod]
	[Description("Regression guard: verifies explicit help target on hidden context still works so hidden scopes remain routable.")]
	public void When_RequestingHelpForHiddenContextPath_Then_HelpIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Context("admin", admin =>
		{
			admin.Map("reset", () => "done").WithDescription("Reset state");
		}).Hidden();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["admin", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("reset");
		output.Text.Should().Contain("Reset state");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting command help so that usage and description are rendered.")]
	public void When_RequestingCommandHelp_Then_UsageAndDescriptionAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Usage: contact list");
		output.Text.Should().Contain("Description: List contacts");
	}

	[TestMethod]
	[Description("A hidden legacy alias remains a parser fallback but never appears in command help, aggregate documentation, or typo suggestions; attribute and fluent forms keep the canonical token visible.")]
	public void When_RouteAliasIsHidden_Then_OnlyParsingRetainsIt()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"deploy",
			static string ([ReplOption(Name = "tenant", Aliases = ["--ACCOUNT"], HiddenAliases = ["--account"])] string? tenant = null) => tenant ?? "none");
		sut.Map(
				"publish",
				static string ([ReplOption(Name = "tenant", Aliases = ["--account", "--ACCOUNT"])] string? tenant = null) => tenant ?? "none")
			.WithOption("tenant", static option => option.HiddenAlias("--account"));

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var fluentHelp = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--no-logo"]));
		var legacy = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--account", "acme", "--no-logo"]));
		var fluentLegacy = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--account", "acme", "--no-logo"]));
		var uppercaseAlias = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--ACCOUNT", "north", "--no-logo"]));
		var typo = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--accoun", "acme", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands
			.Single(command => string.Equals(command.Path, "deploy", StringComparison.Ordinal))
			.Options.Single();

		help.Text.Should().Contain("--tenant");
		help.Text.Should().NotContain("--account");
		help.Text.Should().Contain("--ACCOUNT");
		fluentHelp.Text.Should().Contain("--tenant");
		fluentHelp.Text.Should().Contain("--ACCOUNT");
		fluentHelp.Text.Should().NotContain("--account");
		legacy.ExitCode.Should().Be(0, legacy.Text);
		legacy.Text.Should().Contain("acme");
		fluentLegacy.ExitCode.Should().Be(0, fluentLegacy.Text);
		fluentLegacy.Text.Should().Contain("acme");
		uppercaseAlias.ExitCode.Should().Be(0, uppercaseAlias.Text);
		uppercaseAlias.Text.Should().Contain("north");
		typo.Text.Should().NotContain("Did you mean '--account'");
		option.Name.Should().Be("tenant");
		option.Aliases.Should().Contain(["--tenant", "--ACCOUNT"]);
		option.Aliases.Should().NotContain("--account");
	}

	[TestMethod]
	[Description("ReplOptionAttribute.HiddenAliases can be explicitly set to null (valid attribute metadata despite the nullable warning). Mapping a command with at least one visible alias must not depend on the implicit null-array-to-Span conversion happening to make '.Contains' return false — that reads as a dereference of a null array and should stay explicit (?? []), matching every other consumer of this field.")]
	public void When_HiddenAliasesIsExplicitlyNull_Then_MappingDoesNotThrowAndAliasStaysVisible()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"deploy",
			static string ([ReplOption(Aliases = ["--tenant-name"], HiddenAliases = null!)] string? tenant = null) => tenant ?? "none");

		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		option.Aliases.Should().Contain("--tenant-name");
	}

	[TestMethod]
	[Description("Same null-HiddenAliases contract for an options-group property, which builds its schema entries through a separate code path from direct handler parameters.")]
	public void When_GroupPropertyHiddenAliasesIsExplicitlyNull_Then_MappingDoesNotThrowAndAliasStaysVisible()
	{
		var sut = ReplApp.Create();
		sut.Map("deploy", (NullHiddenAliasesOptions options) => options.Tenant ?? "none");

		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		option.Aliases.Should().Contain("--tenant-name");
	}

	[ReplOptionsGroup]
	private sealed class NullHiddenAliasesOptions
	{
		[ReplOption(Aliases = ["--tenant-name"], HiddenAliases = null!)]
		public string? Tenant { get; set; }
	}

	[TestMethod]
	[Description("Fluent alias visibility follows a route option's case-insensitive override: parser-equivalent aliases are all hidden from help and documentation but remain accepted by parsing under a case-sensitive global default.")]
	public void When_FluentAliasesUseCaseInsensitiveOverride_Then_AllEquivalentSpellingsAreHiddenFromDiscovery()
	{
		var sut = ReplApp.Create();
		sut.Map(
				"publish",
				static string ([ReplOption(Name = "tenant", Aliases = ["--account", "--ACCOUNT"], CaseSensitivity = ReplCaseSensitivity.CaseInsensitive)] string? tenant = null) => tenant ?? "none")
			.WithOption("tenant", static option => option.HiddenAlias("--account"));

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--no-logo"]));
		var lower = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--account", "south", "--no-logo"]));
		var upper = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--ACCOUNT", "north", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--tenant").And.NotContain("--account").And.NotContain("--ACCOUNT");
		option.Aliases.Should().Contain("--tenant").And.NotContain(["--account", "--ACCOUNT"]);
		lower.ExitCode.Should().Be(0, lower.Text);
		lower.Text.Should().Contain("south");
		upper.ExitCode.Should().Be(0, upper.Text);
		upper.Text.Should().Contain("north");
	}

	[TestMethod]
	[Description("After mapping under the sensitive default, switching to case-insensitive mode and unhiding through opposite casing restores one deduplicated discovery representative while both spellings remain parsable.")]
	public void When_GlobalCaseModeChangesAfterMappingAndAliasIsUnhidden_Then_OneEquivalentSpellingReturnsToDiscovery()
	{
		var sut = ReplApp.Create();
		var command = sut.Map(
			"publish",
			static string ([ReplOption(Name = "tenant", Aliases = ["--account", "--ACCOUNT"])] string? tenant = null) => tenant ?? "none");
		sut.Options(static options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		command.WithOption("tenant", static option =>
		{
			option.HiddenAlias("--account");
			option.HiddenAlias("--ACCOUNT", isHidden: false);
		});

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--no-logo"]));
		var lower = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--account", "south", "--no-logo"]));
		var upper = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--ACCOUNT", "north", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--account");
		option.Aliases.Should().Contain("--account");
		lower.ExitCode.Should().Be(0, lower.Text);
		upper.ExitCode.Should().Be(0, upper.Text);
	}

	[TestMethod]
	[Description("A per-option case-sensitive override wins over a case-insensitive global mode, so hiding one exact fluent alias leaves its case-distinct sibling discoverable and both exact spellings parsable.")]
	public void When_FluentAliasUsesSensitiveOverrideUnderInsensitiveGlobal_Then_OnlyExactSpellingIsHiddenFromDiscovery()
	{
		var sut = ReplApp.Create();
		sut.Options(static options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map(
				"publish",
				static string ([ReplOption(Name = "tenant", Aliases = ["--account", "--ACCOUNT"], CaseSensitivity = ReplCaseSensitivity.CaseSensitive)] string? tenant = null) => tenant ?? "none")
			.WithOption("tenant", static option => option.HiddenAlias("--account"));

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--no-logo"]));
		var lower = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--account", "south", "--no-logo"]));
		var upper = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--ACCOUNT", "north", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().NotContain("--account").And.Contain("--ACCOUNT");
		option.Aliases.Should().NotContain("--account").And.Contain("--ACCOUNT");
		lower.ExitCode.Should().Be(0, lower.Text);
		upper.ExitCode.Should().Be(0, upper.Text);
	}

	[TestMethod]
	[Description("A manually registered global alias can be hidden independently: root help omits only that alias while the pre-routing parser still accepts it and exposes its value through the global accessor.")]
	public void When_GlobalAliasIsHidden_Then_HelpOmitsItButParsingRetainsIt()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--account"]);
			options.Parsing.GlobalOption("tenant").HiddenAlias("--account");
		});
		sut.Map("show", static string (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var legacy = ConsoleCaptureHelper.Capture(() => sut.Run(["--account", "acme", "show", "--no-logo"]));

		help.Text.Should().Contain("--tenant");
		help.Text.Should().NotContain("--account");
		legacy.ExitCode.Should().Be(0, legacy.Text);
		legacy.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global hide followed by opposite-case unhide uses the current case-insensitive comparer, restoring one deduplicated help representative while both spellings remain parsable.")]
	public void When_GlobalAliasIsUnhiddenThroughEquivalentCasing_Then_OneRepresentativeReturnsToHelp()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
			options.Parsing.AddGlobalOption<string>("organization", aliases: ["--legacy-org", "--LEGACY-ORG"]));
		sut.Options(options =>
		{
			options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive;
			options.Parsing.GlobalOption("organization").HiddenAlias("--legacy-org");
			options.Parsing.GlobalOption("organization").HiddenAlias("--LEGACY-ORG", isHidden: false);
		});
		sut.Map("show", static string (IGlobalOptionsAccessor globals) =>
			globals.GetValue<string>("organization") ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var lower = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["--legacy-org", "south", "show", "--no-logo"]));
		var upper = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["--LEGACY-ORG", "north", "show", "--no-logo"]));

		help.Text.Should().Contain("--legacy-org");
		lower.ExitCode.Should().Be(0, lower.Text);
		lower.Text.Should().Contain("south");
		upper.ExitCode.Should().Be(0, upper.Text);
		upper.Text.Should().Contain("north");
	}

	[TestMethod]
	[Description("A global registered under case-sensitive parsing with canonical --tenant and a case-distinct hidden alias --TENANT must keep its canonical token visible after switching to case-insensitive parsing: the canonical token's visibility is governed by the definition's own IsHidden, never by HiddenAliases becoming case-equivalent to it.")]
	public void When_CaseDistinctHiddenAliasBecomesEquivalentToCanonicalAfterModeChange_Then_CanonicalStaysVisible()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
			options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--TENANT"]));
		sut.Options(options => options.Parsing.GlobalOption("tenant").HiddenAlias("--TENANT"));
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map("show", static string (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));
		var canonical = ConsoleCaptureHelper.Capture(() => sut.Run(["--tenant", "acme", "show", "--no-logo"]));

		help.Text.Should().Contain("--tenant");
		help.Text.Should().NotContain("--TENANT");
		canonical.ExitCode.Should().Be(0, canonical.Text);
		canonical.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("A hidden command option stays bindable when explicitly provided but is omitted from command help.")]
	public void When_CommandOptionIsHidden_Then_HelpOmitsItAndExplicitInvocationStillBinds()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"deploy",
			([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internal-mode")] bool internalMode = false) =>
				$"{environment}:{internalMode}")
			.WithOption("internalMode", static option => option.Hidden());

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["deploy", "--environment", "prod", "--internal-mode", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--environment");
		help.Text.Should().NotContain("--internal-mode");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("prod:True");
	}

	[TestMethod]
	[Description("Command help must follow global-parser precedence token by token: omit a route canonical token owned by a hidden global, retain a route-owned alias, and leave that alias directly invocable.")]
	public void When_HiddenGlobalOwnsRouteCanonicalToken_Then_CommandHelpKeepsOnlyTheRouteAlias()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Aliases = ["-t"])] string? tenant = null) => tenant ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "-t", "acme", "--no-logo"]));

		help.ExitCode.Should().Be(0, help.Text);
		help.Text.Should().Contain("-t");
		help.Text.Should().NotContain("--tenant");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global case-insensitive parsing makes case-equivalent visible and hidden aliases one logical token; hidden precedence keeps both spellings out of help/docs while parsing remains backwards-compatible.")]
	public void When_GlobalCaseInsensitiveModeMakesVisibleAliasEquivalentToHiddenAlias_Then_HiddenPrecedenceWins()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map(
			"deploy",
			static string ([ReplOption(
				Name = "tenant",
				Aliases = ["--ACCOUNT"],
				HiddenAliases = ["--account"])] string? tenant = null) => tenant ?? "none");

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--ACCOUNT", "acme", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--tenant");
		help.Text.Should().NotContain("--account");
		help.Text.Should().NotContain("--ACCOUNT");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
		option.Aliases.Should().ContainSingle().Which.Should().Be("--tenant");
	}

	[TestMethod]
	[Description("The programmatic-only axis must not leak into human surfaces: an AutomationHidden option stays listed in command help and binds normally from the command line. Only the MCP tool schema drops it.")]
	public void When_CommandOptionIsAutomationHidden_Then_HelpStillListsItAndItBinds()
	{
		var sut = ReplApp.Create();
		sut.Map(
				"deploy",
				([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "internal-mode")] bool internalMode = false) =>
					$"{environment}:{internalMode}")
			.WithOption("internalMode", static option => option.AutomationHidden());

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["deploy", "--environment", "prod", "--internal-mode", "--no-logo"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().Contain("--internal-mode");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("prod:True");
	}

	[TestMethod]
	[Description("A direct handler option may receive an external provider only after fluent metadata is complete, so Hidden() must defer the provider-dependent decision. Aggregate discovery without a fallback then rejects the impossible hidden contract before omitting the option.")]
	public void When_RequiredCommandOptionIsHiddenFluentlyWithoutAService_Then_AggregateDocumentationThrows()
	{
		var sut = ReplApp.Create();
		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "internal-token", Arity = ReplArity.ExactlyOne)] string internalToken) => internalToken);

		var hide = () => command.WithOption("internalToken", option => option.Hidden());
		hide.Should().NotThrow();
		var document = () => sut.CreateDocumentationModel();

		document.Should().Throw<InvalidOperationException>()
			.WithMessage("Option target 'internalToken' (rendered as '--internal-token') for command 'deploy' cannot be hidden because it is required. Either drop the Required/Arity constraint on the option, or hide a different one.");
	}

	[TestMethod]
	[Description("A service registration that throws on resolution — a scoped registration resolved from the root provider, or any constructor that fails — must be treated as unavailable rather than letting that registration's own exception crash discovery. The caller must see the standard hidden-required diagnosis, not whatever the broken registration happened to throw.")]
	public void When_ServiceFallbackRegistrationThrowsOnResolution_Then_TreatedAsUnavailable()
	{
		var sut = ReplApp.Create(services =>
			services.AddSingleton<string>(_ => throw new InvalidOperationException("Simulated resolution failure")));
		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "internal-token", Arity = ReplArity.ExactlyOne)] string internalToken) => internalToken);
		command.WithOption("internalToken", option => option.Hidden());

		var document = () => sut.CreateDocumentationModel();

		document.Should().Throw<InvalidOperationException>()
			.WithMessage("Option target 'internalToken' (rendered as '--internal-token') for command 'deploy' cannot be hidden because it is required.*");
	}

	[TestMethod]
	[Description("A registered service is an established omission fallback in HandlerArgumentBinder and runs before the explicit-arity failure. Both fluent and declarative hiding must therefore remain legal: callers can omit the options and DI still supplies the handler values.")]
	public void When_RequiredOptionTypeHasAServiceFallback_Then_HidingAndOmissionAreAllowed()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<string>("service-token"));
		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Mode = ReplParameterMode.OptionOnly)] string token) => token);

		var fluentAct = () => command.WithOption("token", static option => option.Hidden());
		var declarativeAct = () => sut.Map(
			"publish",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true, Mode = ReplParameterMode.OptionOnly)] string token) => token);

		fluentAct.Should().NotThrow();
		declarativeAct.Should().NotThrow();
		var deployHelp = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var publishHelp = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--no-logo"]));
		var markdownHelp = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--output:markdown", "--no-logo"]));
		var jsonHelp = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--help", "--output:json", "--no-logo"]));
		var deploy = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--no-logo"]));
		var publish = ConsoleCaptureHelper.Capture(() => sut.Run(["publish", "--no-logo"]));
		deployHelp.ExitCode.Should().Be(0, deployHelp.Text);
		deployHelp.Text.Should().NotContain("--token");
		publishHelp.ExitCode.Should().Be(0, publishHelp.Text);
		publishHelp.Text.Should().NotContain("--token");
		markdownHelp.ExitCode.Should().Be(0, markdownHelp.Text);
		markdownHelp.Text.Should().NotContain("--token");
		jsonHelp.ExitCode.Should().Be(0, jsonHelp.Text);
		jsonHelp.Text.Should().NotContain("--token");
		deploy.ExitCode.Should().Be(0, deploy.Text);
		deploy.Text.Should().Contain("service-token");
		publish.ExitCode.Should().Be(0, publish.Text);
		publish.Text.Should().Contain("service-token");
	}

	[TestMethod]
	[Description("IProgress<T> is synthesized from IReplInteractionChannel, as CancellationToken is supplied by Repl: it is never an option, in help or in the documentation model, and the handler still receives it.")]
	public void When_AHandlerReceivesProgress_Then_ItIsNeverAnOption()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"sync",
			(IProgress<double> progress, IProgress<ReplProgressEvent> events) =>
				progress is not null && events is not null ? "progress-ready" : "missing");

		var document = sut.CreateDocumentationModel();
		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		document.Commands.Single(command => string.Equals(command.Path, "sync", StringComparison.Ordinal))
			.Options.Should().BeEmpty();
		help.ExitCode.Should().Be(0, help.Text);
		help.Text.Should().NotContain("--progress").And.NotContain("--events");
		invocation.ExitCode.Should().Be(0);
		invocation.Text.Should().Contain("progress-ready");
	}

	[TestMethod]
	[Description("The DI-enabled facade must build programmatic documentation with the same shared provider used by Run. Otherwise a configured service-backed hidden option runs successfully but aggregate documentation rejects it as impossible.")]
	public void When_ProgrammaticDocumentationUsesConfiguredServices_Then_HiddenRequiredOptionIsAllowed()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<string>("service-token"));
		sut.Map(
			"deploy",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true)] string token) => token);

		var document = () => sut.CreateDocumentationModel();

		document.Should().NotThrow();
	}

	[TestMethod]
	[Description("Visible option requiredness in programmatic documentation must use the configured service provider too, matching the argument-free invocation that HandlerArgumentBinder accepts.")]
	public void When_ProgrammaticDocumentationUsesConfiguredServices_Then_VisibleRequiredOptionIsOptional()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<string>("service-token"));
		sut.Map(
			"deploy",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne)] string token) => token);

		var model = sut.CreateDocumentationModel();
		var option = model.Commands
			.Single(command => string.Equals(command.Path, "deploy", StringComparison.Ordinal))
			.Options.Single();

		option.Required.Should().BeFalse();
	}

	[TestMethod]
	[Description("Programmatic documentation needs the same explicit-provider seam as Run so externally managed/custom providers supplied after mapping can determine hidden-option invocability.")]
	public void When_ProgrammaticDocumentationUsesAnExternalProvider_Then_RequiredHiddenOptionIsAllowed()
	{
		using var services = new ServiceCollection()
			.AddSingleton<string>("external-token")
			.BuildServiceProvider();
		var sut = ReplApp.Create();
		sut.Map(
			"deploy",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true)] string token) => token);
		var document = () => sut.CreateDocumentationModel(services);

		document.Should().NotThrow();
	}

	[TestMethod]
	[Description("An external provider is supplied only at Run time, after mapping and fluent metadata are complete. Required hidden validation must therefore defer its provider-dependent decision and let the binder use that provider when the option is omitted.")]
	public void When_RequiredHiddenOptionUsesAnExternalServiceProvider_Then_OmissionIsAllowed()
	{
		using var services = new ServiceCollection()
			.AddSingleton<string>("external-token")
			.BuildServiceProvider();
		var sut = ReplApp.Create();

		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true)] string token) => token);
		var run = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--no-logo"], services));

		command.Should().NotBeNull();
		run.ExitCode.Should().Be(0, run.Text);
		run.Text.Should().Contain("external-token");
	}

	[TestMethod]
	[Description("Custom globals parse before route options. Aggregate documentation must omit globally owned route tokens so MCP cannot advertise an argument that execution will bind to the global instead; unrelated route options remain visible.")]
	public void When_HiddenGlobalOwnsRouteOptionToken_Then_AggregateDocumentationOmitsThatRouteOption()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string (
				[ReplOption] string? tenant = null,
				[ReplOption] string? region = null) => $"{tenant}:{region}");

		var command = sut.CreateDocumentationModel().Commands.Single(candidate =>
			string.Equals(candidate.Path, "deploy", StringComparison.Ordinal));

		command.Options.Should().NotContain(option => string.Equals(option.Name, "tenant", StringComparison.Ordinal));
		command.Options.Should().Contain(option => string.Equals(option.Name, "region", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("A globally owned secondary route alias must be removed from documentation without changing the route option's canonical MCP argument name, which the adapter reconstructs as a long option.")]
	public void When_HiddenGlobalOwnsSecondaryRouteAlias_Then_DocumentationKeepsOnlyTheCanonicalRouteToken()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("t");
			options.Parsing.GlobalOption("t").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Aliases = ["--t"])] string? tenant = null) => tenant ?? "none");

		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		option.Name.Should().Be("tenant");
		option.Aliases.Should().Contain("--tenant");
		option.Aliases.Should().NotContain("--t");
	}

	[TestMethod]
	[Description("A route option remains documentable when its canonical token belongs to a global but an alias remains route-owned. The semantic programmatic name stays stable while the surviving alias remains the exact CLI invocation token.")]
	public void When_HiddenGlobalOwnsRouteCanonicalToken_Then_DocumentationKeepsStableNameAndRouteAlias()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Aliases = ["-t"])] string? tenant = null) => tenant ?? "none");

		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		option.Name.Should().Be("tenant");
		option.Aliases.Should().ContainSingle().Which.Should().Be("-t");
	}

	[TestMethod]
	[Description("If a global shadows the only canonical token of a required route option, omitting the unreachable field would advertise a dead programmatic contract. Aggregate discovery must fail closed unless a runtime fallback can satisfy it.")]
	public void When_HiddenGlobalOwnsRequiredRouteOptionToken_Then_AggregateDocumentationFailsClosed()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Arity = ReplArity.ExactlyOne)] string tenant) => tenant);

		var act = () => sut.CreateDocumentationModel();

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'tenant'*'--tenant'*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("If a hidden global owns the only token of a required options-group property, command help must fail closed instead of omitting the unreachable option from an apparently usable command.")]
	public void When_HiddenGlobalOwnsRequiredGroupOptionToken_Then_CommandHelpFailsClosed()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map("deploy", static string (RequiredTenantOptions options) => options.Tenant);

		var help = () => sut.Run(["deploy", "--help", "--no-logo"]);

		help.Should().Throw<InvalidOperationException>()
			.WithMessage("*Tenant*--tenant*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("A globally shadowed named token does not make an OptionAndPositional group property unreachable: positional binding still satisfies its lower bound, so command help remains available and execution accepts the positional value.")]
	public void When_HiddenGlobalOwnsOptionAndPositionalGroupToken_Then_CommandHelpAndPositionalInvocationRemainValid()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map("deploy", static string (PositionalTenantOptions options) => options.Tenant);

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "acme", "--no-logo"]));

		help.ExitCode.Should().Be(0, help.Text);
		help.Text.Should().NotContain("--tenant");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("A hidden global cannot satisfy a direct OptionOnly parameter when the parser rejects the duplicate global/route token. If that token is the only route spelling and no provider fallback exists, command help must fail closed instead of describing no usable invocation.")]
	public void When_HiddenGlobalOwnsRequiredDirectOptionToken_Then_CommandHelpFailsClosed()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Arity = ReplArity.ExactlyOne, Mode = ReplParameterMode.OptionOnly)] string tenant) => tenant);

		foreach (var format in new[] { "human", "markdown", "json" })
		{
			var help = () => sut.Run(["deploy", "--help", $"--output:{format}", "--no-logo"]);

			help.Should().Throw<InvalidOperationException>()
				.WithMessage("*tenant*--tenant*cannot be hidden because it is required*");
		}
	}

	[TestMethod]
	[Description("A wholly hidden direct OptionOnly parameter with no active service fallback is still required by execution. Command help must reject that impossible visible contract just like aggregate documentation does.")]
	public void When_RequiredDirectOptionIsHiddenWithoutService_Then_CommandHelpFailsClosed()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"deploy",
			static string ([ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true, Mode = ReplParameterMode.OptionOnly)] string token) => token);

		var help = () => sut.Run(["deploy", "--help", "--no-logo"]);

		help.Should().Throw<InvalidOperationException>()
			.WithMessage("*token*--token*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("A direct OptionAndPositional parameter remains reachable through positional binding when a hidden global owns its named token, so command help and positional execution must remain valid.")]
	public void When_HiddenGlobalOwnsOptionAndPositionalDirectToken_Then_CommandHelpAndPositionalInvocationRemainValid()
	{
		var sut = ReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("tenant");
			options.Parsing.GlobalOption("tenant").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(Arity = ReplArity.ExactlyOne, Mode = ReplParameterMode.OptionAndPositional)] string tenant) => tenant);

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "acme", "--no-logo"]));

		help.ExitCode.Should().Be(0, help.Text);
		help.Text.Should().NotContain("--tenant");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("DI fallback applies to direct handler parameters only. The binder constructs an options group before its service fallback, so registering the same property type must not make a hidden required group property look invocable.")]
	public void When_HiddenRequiredGroupPropertyTypeIsRegisteredAsAService_Then_MappingStillThrows()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<string>("service-token"));

		var act = () => sut.Map("deploy", (HiddenRequiredOptions options) => options.Token);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'Token'*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("The typo suggester is a discovery surface like any other. Searching the full token set turns a validation error into a way to enumerate hidden options by probing at small edit distance, which defeats hiding on the one surface that has no listing of its own.")]
	public void When_MistypingAHiddenOption_Then_TheSuggestionDoesNotRevealIt()
	{
		var sut = ReplApp.Create();
		sut.Map(
				"deploy",
				([ReplOption(Name = "environment")] string environment, [ReplOption(Name = "secret")] string? secret = null) =>
					$"{environment}:{secret}")
			.WithOption("secret", static option => option.Hidden());

		var hidden = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["deploy", "--environment", "prod", "--secre", "x", "--no-logo"]));
		var visible = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["deploy", "--environmnet", "prod", "--no-logo"]));

		hidden.Text.Should().NotContain("--secret");
		visible.Text.Should().Contain("--environment", "a visible option is still suggested, so the filter is not simply disabling suggestions");
	}

	[TestMethod]
	[Description("A hidden global that owns a visible route token must suppress that unreachable token from typo suggestions just as it is suppressed from help, completion, and documentation; an unrelated route option remains a positive-control suggestion.")]
	public void When_HiddenGlobalOwnsVisibleRouteToken_Then_TypoSuggestionDoesNotRevealIt()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<string>("secret");
			options.Parsing.GlobalOption("secret").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption] string? secret = null, [ReplOption] string? region = null) => $"{secret}:{region}");

		var hidden = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--secre", "x", "--no-logo"]));
		var visible = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--regoin", "x", "--no-logo"]));

		hidden.Text.Should().NotContain("Did you mean '--secret'");
		visible.Text.Should().Contain("Did you mean '--region'", "route suggestions must remain enabled");
	}

	[TestMethod]
	[Description("An inherited declarative hidden alias is reevaluated when the global case mode changes after mapping: once the parser considers the visible and hidden spellings equivalent, discovery hides both aliases while the canonical token and parsing remain available.")]
	public void When_GlobalCaseModeChangesAfterMapping_Then_DeclarativeHiddenAliasPrecedenceIsReevaluated()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
			"deploy",
			static string ([ReplOption(Name = "tenant", Aliases = ["--ACCOUNT"], HiddenAliases = ["--account"])] string? tenant = null) => tenant ?? "none");
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--ACCOUNT", "acme", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--tenant").And.NotContain("--account").And.NotContain("--ACCOUNT");
		option.Aliases.Should().ContainSingle().Which.Should().Be("--tenant");
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Human documentation retains an option when a global shadows its ordinary flag but an unowned reverse alias remains invocable; help and execution expose the same reachable reverse token.")]
	public void When_GlobalOwnsOrdinaryFlagButReverseAliasRemains_Then_HumanDocumentationRetainsReverseAlias()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options =>
		{
			options.Parsing.AddGlobalOption<bool>("force");
			options.Parsing.GlobalOption("force").Hidden();
		});
		sut.Map(
			"deploy",
			static string ([ReplOption(ReverseAliases = ["--no-force"])] bool force = true) => force.ToString());

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--help", "--no-logo"]));
		var invocation = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--no-force", "--no-logo"]));
		var option = sut.CreateDocumentationModel().Commands.Single().Options.Single();

		help.Text.Should().Contain("--no-force").And.NotContain("--force");
		option.Aliases.Should().BeEmpty();
		option.ReverseAliases.Should().ContainSingle().Which.Should().Be("--no-force");
		var publicRoundTrip = new Repl.Documentation.ReplDocOption(
			option.Name,
			option.Type,
			option.Required,
			option.Description,
			option.Aliases,
			option.ReverseAliases,
			option.ValueAliases,
			option.EnumValues,
			option.DefaultValue)
		{
			IsHidden = option.IsHidden,
			IsAutomationHidden = option.IsAutomationHidden,
		};
		option.Equals(publicRoundTrip).Should().BeTrue("MCP-only derivations must not alter public record equality");
		option.GetHashCode().Should().Be(publicRoundTrip.GetHashCode());
		invocation.ExitCode.Should().Be(0, invocation.Text);
		invocation.Text.Should().Contain("False");
	}

	[TestMethod]
	[Description("A nullable reference option with no default has its arity inferred as ExactlyOne, but arity governs how many values the token consumes when present — omitting it binds null perfectly well. Hiding it must therefore be allowed; only an explicitly declared required arity should override what the CLR shape says.")]
	public void When_HidingAnOmittableOptionWithInferredArity_Then_ItIsAllowed()
	{
		var sut = ReplApp.Create();
		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "token", Mode = ReplParameterMode.OptionOnly)] string? token) => token ?? "none");

		var act = () => command.WithOption("token", static option => option.Hidden());

		act.Should().NotThrow();
		var run = ConsoleCaptureHelper.Capture(() => sut.Run(["deploy", "--no-logo"]));
		run.ExitCode.Should().Be(0, run.Text);
		run.Text.Should().Contain("none");
	}

	[TestMethod]
	[Description("A non-defaulted bool in OptionAndPositional mode may still come from an external provider, so hiding is deferred. Without such a provider, aggregate discovery rejects it because MCP cannot express the positional fallback for an omitted hidden option.")]
	public void When_HidingANonOmittableFlagInPositionalModeWithoutAService_Then_AggregateDocumentationThrows()
	{
		var sut = ReplApp.Create();
		var command = sut.Map("deploy", ([ReplOption(Name = "force")] bool force) => force.ToString());

		command.WithOption("force", static option => option.Hidden());
		var act = () => sut.CreateDocumentationModel();

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'force'*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("A non-nullable value-type option with no default requires either a token or a service. Fluent hiding remains compatible with a provider supplied later, but aggregate discovery without that provider must reject the otherwise impossible command.")]
	public void When_HidingAnOptionThatCannotBeOmittedWithoutAService_Then_AggregateDocumentationThrows()
	{
		var sut = ReplApp.Create();
		var command = sut.Map(
			"deploy",
			([ReplOption(Name = "force", Mode = ReplParameterMode.OptionOnly)] bool force) => force.ToString());

		command.WithOption("force", static option => option.Hidden());
		var act = () => sut.CreateDocumentationModel();

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'force'*cannot be hidden because it is required*");
	}

	[TestMethod]
	[Description("Declarative hiding has the same provider timing as fluent hiding: Map cannot reject a direct parameter before an external provider is known. Aggregate discovery later evaluates the active provider and rejects the no-service contract.")]
	public void When_RequiredCommandOptionIsHiddenByAttributeWithoutAService_Then_AggregateDocumentationThrows()
	{
		var sut = ReplApp.Create();

		var map = () => sut.Map(
			"deploy",
			([ReplOption(Name = "internal-token", Arity = ReplArity.ExactlyOne, Hidden = true)] string internalToken) => internalToken);
		map.Should().NotThrow();
		var document = () => sut.CreateDocumentationModel();

		document.Should().Throw<InvalidOperationException>()
			.WithMessage("Option target 'internalToken' (rendered as '--internal-token') for command 'deploy' cannot be hidden because it is required. Either drop the Required/Arity constraint on the option, or hide a different one.");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting command help for aliased command so that aliases are shown.")]
	public void When_RequestingCommandHelpForAliasedCommand_Then_AliasesAreShown()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithDescription("List contacts")
			.WithAlias("ls", "l");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Aliases: ls, l");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting scoped help on dynamic route so that sub commands are listed.")]
	public void When_RequestingScopedHelpOnDynamicRoute_Then_SubCommandsAreListed()
	{
		var sut = ReplApp.Create();
		sut.Map("contact {id:int} show", () => "ok")
			.WithDescription("Show contact");
		sut.Map("contact {id:int} remove", () => "ok")
			.WithDescription("Remove contact");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "42", "--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("show");
		output.Text.Should().Contain("remove");
	}

	[TestMethod]
	[Description("Regression guard: verifies context has description attribute so that help uses context description.")]
	public void When_ContextHasDescriptionAttribute_Then_HelpUsesContextDescription()
	{
		var sut = ReplApp.Create();
		sut.Context("contact",
			[System.ComponentModel.Description("Manage contacts")]
			(IReplMap context) =>
			{
				context.Map("list", () => "ok");
			});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("contact ...");
		output.Text.Should().Contain("Manage contacts");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting help in json format so that help becomes machine readable.")]
	public void When_RequestingRootHelpInJson_Then_HelpIsMachineReadable()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--json"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"scope\": \"root\"");
		output.Text.Should().Contain("\"commands\":");
		output.Text.Should().Contain("\"name\": \"contact ...\"");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting command help in yaml format so that help stays machine readable outside json.")]
	public void When_RequestingCommandHelpInYaml_Then_HelpIsMachineReadable()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--help", "--yaml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("scope: 'contact list'");
		output.Text.Should().Contain("commands:");
		output.Text.Should().Contain("usage: 'contact list'");
	}

	[TestMethod]
	[Description("Regression guard: verifies machine-readable command help keeps aliases so automated clients can discover shorthand invocations.")]
	public void When_RequestingCommandHelpInJsonForAliasedCommand_Then_AliasesAreIncluded()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithDescription("List contacts")
			.WithAlias("ls", "l");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--help", "--json"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\"aliases\": [");
		output.Text.Should().Contain("\"ls\"");
		output.Text.Should().Contain("\"l\"");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting help in xml format so that structured help is available to non-json consumers.")]
	public void When_RequestingRootHelpInXml_Then_HelpIsMachineReadable()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--xml"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("<HelpDocumentModel>");
		output.Text.Should().Contain("<scope>root</scope>");
		output.Text.Should().Contain("<name>contact ...</name>");
	}

	[TestMethod]
	[Description("Regression guard: verifies requesting help with unknown format so that execution fails with clear output format guidance.")]
	public void When_RequestingHelpWithUnknownFormat_Then_CommandFailsWithExplicitError()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["--help", "--output:toml"]));

		output.ExitCode.Should().Be(2);
		output.StdErr.Should().Contain("Error: unknown output format 'toml'.");
	}

	[TestMethod]
	[Description("Regression guard: verifies '?' interactive ambient alias so that users can request help with a single keystroke command.")]
	public void When_InteractiveInputIsQuestionMark_Then_HelpIsRendered()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.CaptureWithInput("?\nexit\n", () => sut.Run(Array.Empty<string>()));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("contact list");
	}

	[TestMethod]
	[Description("Repl.Spectre ships separately and can be compiled against an older Repl.Core. Keep the legacy five-argument help factory and BuildRenderModel call shape so upgrading only Core does not fail with MissingMethodException.")]
	public void When_ReplSpectreUsesLegacyHelpFactoryAbi_Then_TheFiveArgumentCallShapeRemainsAvailable()
	{
		HelpOutputFactory factory = static (routes, contexts, scopeTokens, parsingOptions, ambientOptions) =>
			HelpTextBuilder.BuildRenderModel(routes, contexts, scopeTokens, parsingOptions, ambientOptions);

		factory.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive help uses the active output format so Spectre defaults are respected.")]
	public void When_InteractiveHelpAndSpectreIsDefault_Then_SpectreHelpIsRendered()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole()
			.UseDefaultInteractive();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.CaptureWithInput("?\nexit\n", () => sut.Run(Array.Empty<string>()));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("contact");
		output.Text.Should().Contain("Global Commands");
		output.Text.Should().NotContain("Global Commands:");
	}

	[TestMethod]
	[Description("Regression guard: verifies partial command help with dynamic continuation so that command usage is rendered instead of scoped/global command list.")]
	public void When_RequestingHelpForLiteralPrefixWithDynamicArguments_Then_CommandUsageIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("add {name} {email:email}", () => "ok")
			.WithDescription("Add a new contact.");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["add", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Usage: add <name> <email>");
		output.Text.Should().Contain("Description: Add a new contact.");
		output.Text.Should().NotContain("Global Commands:");
	}

	[TestMethod]
	[Description("Regression guard: verifies help path prefers literal command segments over dynamic context captures when both can match.")]
	public void When_RequestingHelpForLiteralPathThatAlsoMatchesDynamicContext_Then_LiteralBranchIsPreferred()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", contact =>
		{
			contact.Map("add {name} {email:email}", () => "ok").WithDescription("Add a contact");
			contact.Map("list", () => "ok").WithDescription("List all contacts");
			contact.Context("{name}", scoped =>
			{
				scoped.Map("remove", () => "ok").WithDescription("Remove this contact");
				scoped.Map("show", () => "ok").WithDescription("Show contact details");
			});
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "add", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Usage: contact add <name> <email>");
		output.Text.Should().Contain("Description: Add a contact");
		output.Text.Should().NotContain("remove");
		output.Text.Should().NotContain("show");
	}

	[TestMethod]
	[Description("Regression guard: verifies command has overloads so that help for the literal prefix lists all overload signatures.")]
	public void When_RequestingHelpForCommandWithOverloads_Then_AllOverloadsAreListed()
	{
		var sut = ReplApp.Create();
		sut.Map("add {name} {email:email}", () => "ok")
			.WithDescription("Add contact by identity.");
		sut.Map("add {id:int}", () => "ok")
			.WithDescription("Add contact by id.");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["add", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Commands:");
		output.Text.Should().Contain("add <id>");
		output.Text.Should().Contain("add <name> <email>");
		output.Text.Should().Contain("Add contact by id.");
		output.Text.Should().Contain("Add contact by identity.");
		output.Text.Should().NotContain("Global Commands:");
	}

	[TestMethod]
	[Description("Regression guard: verifies ANSI forced help rendering so that scoped and global command lists are colorized.")]
	public void When_HelpUsesAnsiModeAlways_Then_HelpContainsAnsiSequences()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("\u001b[");
	}

	[TestMethod]
	[Description("Regression guard: verifies help separates commands from scopes so discovery output distinguishes executable routes from contexts.")]
	public void When_RequestingRootHelpWithContextsAndCommands_Then_HelpSeparatesCommandsAndScopes()
	{
		var sut = ReplApp.Create();
		sut.Map("version", () => "1.0.0").WithDescription("Show app version");
		sut.Context("contact", context =>
		{
			context.Map("list", () => "ok").WithDescription("List contacts");
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Commands:");
		output.Text.Should().Contain("version");
		output.Text.Should().Contain("Scopes:");
		output.Text.Should().Contain("contact ...");
		output.Text.Should().NotContain("(none)");
	}

	[TestMethod]
	[Description("Regression guard: verifies help with only scopes omits empty command section so output stays focused.")]
	public void When_RequestingRootHelpWithOnlyScopes_Then_CommandsSectionIsOmitted()
	{
		var sut = ReplApp.Create();
		sut.Context("contact", context =>
		{
			context.Map("list", () => "ok").WithDescription("List contacts");
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.TrimStart().Should().StartWith("Scopes:");
		output.Text.Should().Contain("Scopes:");
		output.Text.Should().Contain("contact ...");
		output.Text.Should().NotContain("(none)");
	}

	[TestMethod]
	[Description("Regression guard: verifies embedded profile help hides exit ambient command when exit is disabled.")]
	public void When_RequestingRootHelpWithEmbeddedProfile_Then_ExitAmbientCommandIsHidden()
	{
		var sut = ReplApp.Create().UseEmbeddedConsoleProfile();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Global Commands:");
		output.Text.Should().Contain("help [path]");
		output.Text.Should().NotMatchRegex(@"(?m)^\s*exit(\s|$)");
	}

	[TestMethod]
	[Description("Regression guard: verifies command help shows parameter descriptions from [Description] attributes on handler parameters.")]
	public void When_RequestingCommandHelpWithParameterDescriptions_Then_ParameterSectionIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("send {message}", (Func<string, string>)SendHandler)
			.WithDescription("Publish a message to all watching sessions");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["send", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Arguments:");
		output.Text.Should().Contain("<message>");
		output.Text.Should().Contain("Message to send to all watching sessions");
	}

	[TestMethod]
	[Description("Regression guard: verifies command help omits Parameters section when no handler parameters have [Description] attributes.")]
	public void When_RequestingCommandHelpWithoutParameterDescriptions_Then_NoParameterSectionIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("ping {host}", (Func<string, string>)(host => host))
			.WithDescription("Ping a remote host");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ping", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Usage: ping <host>");
		output.Text.Should().NotContain("Arguments:");
	}

	[TestMethod]
	[Description("Regression guard: verifies command help renders schema-derived options with aliases and enum placeholders so documentation matches parser capabilities.")]
	public void When_RequestingCommandHelpWithDeclaredOptions_Then_OptionsSectionIncludesAliasesAndEnumValues()
	{
		var sut = ReplApp.Create();
		sut.Map(
			"render",
			([ReplOption(Aliases = ["-m"])] HelpMode mode = HelpMode.Fast,
				[ReplOption(ReverseAliases = ["--no-verbose"])] bool verbose = false) => $"{mode}:{verbose}");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["render", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Options:");
		output.Text.Should().Contain("--mode, -m <Fast|Slow>");
		output.Text.Should().Contain("--verbose, --no-verbose");
	}

	[TestMethod]
	[Description("Regression guard: verifies command help explains result-flow paging controls for paged handlers.")]
	public void When_RequestingCommandHelpForPagedHandler_Then_ResultFlowOptionsAreShown()
	{
		var sut = ReplApp.Create();
		sut.Map("activity", (IReplPagingContext paging) =>
			paging.Page(["one"], nextCursor: "next", totalCount: 2));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["activity", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Result Flow:");
		output.Text.Should().Contain("--result:page-size <n>");
		output.Text.Should().Contain("--result:cursor <value>");
		output.Text.Should().Contain("--result:all");
		output.Text.Should().Contain("--result:pager=auto|off|more|inline|full");
	}

	[TestMethod]
	[Description("Regression guard: verifies Spectre command help explains result-flow paging controls for paged handlers.")]
	public void When_RequestingCommandHelpForPagedHandlerInSpectre_Then_ResultFlowOptionsAreShown()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("activity", (IReplPagingContext paging) =>
			paging.Page(["one"], nextCursor: "next", totalCount: 2));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["activity", "--help", "--spectre", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Result Flow");
		output.Text.Should().Contain("--result:page-size <n>");
	}

	[TestMethod]
	[Description("Regression guard: verifies markdown command help explains result-flow paging controls for paged handlers.")]
	public void When_RequestingCommandHelpForPagedHandlerInMarkdown_Then_ResultFlowOptionsAreShown()
	{
		var sut = ReplApp.Create();
		sut.Map("activity", (IReplPagingContext paging) =>
			paging.Page(["one"], nextCursor: "next", totalCount: 2));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["activity", "--help", "--markdown", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("# `activity`");
		output.Text.Should().Contain("## Result Flow");
		output.Text.Should().Contain("`--result:page-size <n>`");
		output.Text.Should().NotContain("| Field | Value |");
	}

	[TestMethod]
	[Description("Regression guard: verifies command help explains result-flow paging controls for page-source handlers.")]
	public void When_RequestingCommandHelpForPageSourceHandler_Then_ResultFlowOptionsAreShown()
	{
		var sut = ReplApp.Create();
		sut.Map("activity", () => new StaticPageSource<string>());

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["activity", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Result Flow:");
		output.Text.Should().Contain("--result:page-size <n>");
	}

	[TestMethod]
	[Description("Regression guard: verifies result-flow controls stay hidden for handlers that do not support paging.")]
	public void When_RequestingCommandHelpForNonPagedHandler_Then_ResultFlowOptionsAreHidden()
	{
		var sut = ReplApp.Create();
		sut.Map("list", () => SingleResult);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["list", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("Result Flow:");
		output.Text.Should().NotContain("--result:page-size <n>");
	}

	[TestMethod]
	[Description("Regression guard: verifies injected global-options accessor parameters are omitted from command help.")]
	public void When_RequestingCommandHelpWithGlobalOptionsAccessor_Then_AccessorIsNotListedAsCommandOption()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["show", "--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("--globals");
	}

	private static string SendHandler([ComponentDescriptionAttribute("Message to send to all watching sessions")] string message) => message;

	[ReplOptionsGroup]
	private sealed class PositionalTenantOptions
	{
		[ReplOption(Mode = ReplParameterMode.OptionAndPositional, Arity = ReplArity.ExactlyOne)]
		public string Tenant { get; set; } = null!;
	}

	[ReplOptionsGroup]
	private sealed class RequiredTenantOptions
	{
		[ReplOption(Mode = ReplParameterMode.OptionOnly, Arity = ReplArity.ExactlyOne)]
		public string Tenant { get; set; } = null!;
	}

	[ReplOptionsGroup]
	private sealed class HiddenRequiredOptions
	{
		[ReplOption(Name = "token", Arity = ReplArity.ExactlyOne, Hidden = true)]
		public string Token { get; set; } = null!;
	}

	private enum HelpMode
	{
		Fast,
		Slow,
	}

	private sealed class StaticPageSource<T> : IReplPageSource<T>
	{
		public ValueTask<ReplPage<T>> FetchAsync(
			ReplPageRequest request,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new ReplPage<T>(
				[],
				new ReplPageInfo(
					Cursor: request.Cursor,
					NextCursor: null,
					TotalCount: 0,
					PageSize: request.PageSize)));
	}

	[TestMethod]
	[Description("Regression guard: verifies Spectre help uses a dedicated renderer so command help keeps the expected sections.")]
	public void When_RequestingCommandHelpInSpectre_Then_DedicatedHelpSectionsAreRendered()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map(
			"render {target}",
			([ComponentDescriptionAttribute("Target to render")] string target,
				[ReplOption(Aliases = ["-m"])] HelpMode mode = HelpMode.Fast) => $"{target}:{mode}")
			.WithDescription("Render a target")
			.WithAlias("draw")
			.WithAnswer("confirm", "Confirmation answer");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["render", "--help", "--spectre", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Usage");
		output.Text.Should().Contain("Description");
		output.Text.Should().Contain("Aliases");
		output.Text.Should().Contain("Arguments");
		output.Text.Should().Contain("Options");
		output.Text.Should().Contain("Answers");
		output.Text.Should().Contain("Render a target");
		output.Text.Should().NotContain("\"scope\":");
	}

	[TestMethod]
	[Description("Regression guard: verifies --human overrides the Spectre default so the classic text help stays available.")]
	public void When_RequestingHelpWithHumanAliasWhileSpectreIsDefault_Then_ClassicHelpIsRendered()
	{
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("contact list", () => "ok").WithDescription("List contacts");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--human", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Global Commands:");
		output.Text.Should().Contain("contact");
	}
}
