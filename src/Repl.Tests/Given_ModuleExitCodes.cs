namespace Repl.Tests;

[TestClass]
public sealed class Given_ModuleExitCodes
{
	[TestMethod]
	[Description("Verifies a module's own usage-error code applies to a refusal of its commands, while the app's code still applies everywhere else.")]
	public void When_AModuleCommandIsRefused_Then_TheModuleCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new TestsModule());
		sut.Map("other run", () => "ok");

		Run(sut, ["tests", "run", "--bogus"]).Should().Be(5);
		Run(sut, ["other", "run", "--bogus"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies a module's completed-with-failures code applies to its commands only, so a module can follow another tool's convention without changing the app's.")]
	public void When_AModuleCommandCompletesWithFailures_Then_TheModuleCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new TestsModule());
		sut.Map("other check", () => Results.CompletedWithFailures("1 failed"));

		Run(sut, ["tests", "run"]).Should().Be(2);
		Run(sut, ["other", "check"]).Should().Be(1);
	}

	[TestMethod]
	[Description("Verifies each overridable kind maps through its own module code, so swapping two of them, or dropping one, cannot pass unnoticed.")]
	[DataRow(new[] { "kinds", "succeed" }, 10, DisplayName = "Success")]
	[DataRow(new[] { "kinds", "succeed", "--bogus" }, 11, DisplayName = "UsageError")]
	[DataRow(new[] { "kinds", "bind" }, 12, DisplayName = "BindingError")]
	[DataRow(new[] { "kinds", "fail" }, 13, DisplayName = "HandlerError")]
	[DataRow(new[] { "kinds", "throw" }, 14, DisplayName = "HandlerException")]
	[DataRow(new[] { "kinds", "report" }, 15, DisplayName = "CompletedWithFailures")]
	public void When_AModuleOverridesEveryKind_Then_EachKindUsesItsOwnCode(string[] args, int expected)
	{
		var sut = CreateApp();
		sut.MapModule(new EveryKindModule());

		Run(sut, args).Should().Be(expected);
	}

	[TestMethod]
	[Description("Verifies what a module cannot override keeps its code: an explicit exit result keeps its handler's code and help keeps the app's.")]
	public void When_AModuleOverridesSuccess_Then_ExplicitExitAndHelpKeepTheirCodes()
	{
		var sut = CreateApp();
		sut.MapModule(new EveryKindModule());

		Run(sut, ["kinds", "exit"]).Should().Be(0, "an IExitResult keeps the code its handler chose");
		Run(sut, ["kinds", "succeed", "--help"]).Should().Be(0, "help keeps the application's code");
	}

	[TestMethod]
	[Description("Verifies an unknown command under a module's own words is refused with the module's code: the caller was invoking that module, and a tool following dotnet test expects its invalid-command-line code.")]
	public void When_AnUnknownCommandFollowsTheModuleWords_Then_TheModuleCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new TestsModule());
		sut.Map("other run", () => "ok");

		Run(sut, ["tests", "bogus"]).Should().Be(5);
		Run(sut, ["bogus"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies an abbreviation whose every expansion is a command of one module is refused with that module's code, wherever the ambiguous word is and however many words are abbreviated, even next to an app route sharing the first word.")]
	[DataRow(new[] { "t", "run" }, DisplayName = "Ambiguous first word")]
	[DataRow(new[] { "tests", "r" }, DisplayName = "Ambiguous second word")]
	[DataRow(new[] { "t", "r" }, DisplayName = "Two abbreviated words")]
	public void When_EveryExpansionOfAnAmbiguousAbbreviationBelongsToOneModule_Then_TheModuleCodeApplies(string[] args)
	{
		var sut = CreateApp();
		sut.Map("tests status", () => "ok");
		sut.MapModule(new ToolingModule());

		Run(sut, args).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies an abbreviation ambiguous between a module's command and the application's keeps the application's code: no single module owns what it could mean, wherever the expansions were found.")]
	[DataRow("trace run", new[] { "t", "run" }, DisplayName = "One ambiguous word")]
	[DataRow("tests rerun", new[] { "t", "r" }, DisplayName = "An expansion that is itself ambiguous")]
	public void When_TheExpansionsOfAnAmbiguousAbbreviationSpanTheModuleAndTheApp_Then_TheAppCodeApplies(string appRoute, string[] args)
	{
		var sut = CreateApp();
		sut.Map(appRoute, () => "ok");
		sut.MapModule(new RoutesModule(5, "tests run", "tools run"));

		Run(sut, args).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies an expansion that is itself an ambiguous abbreviation is weighed by its own expansions: `tests r` means one of the module's two commands, which outweighs the app word `tests` alone.")]
	public void When_AnExpansionIsItselfAmbiguous_Then_ItsOwnExpansionsAreWeighed()
	{
		var sut = CreateApp();
		sut.Map("tests status", () => "ok");
		sut.MapModule(new RoutesModule(5, "tests run", "tests report", "tools list"));

		Run(sut, ["t", "r"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies the expansions of an ambiguous abbreviation are weighed as unknown commands are: the one matching the most words decides, so a module's whole command outweighs an app word that only starts it.")]
	public void When_OneExpansionOfAnAmbiguousAbbreviationMatchesMoreWords_Then_ItsModuleDecides()
	{
		var sut = CreateApp();
		sut.Map("tools list", () => "ok");
		sut.MapModule(new RoutesModule(5, "tests run"));

		Run(sut, ["t", "run"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a hidden command is not something an abbreviation could expand to: prefix resolution never offers it, so it does not tie with the module's commands.")]
	public void When_AHiddenCommandStartsLikeTheModulesCommands_Then_TheModuleCodeStillApplies()
	{
		var sut = CreateApp();
		sut.Map("trace", () => "ok").Hidden();
		sut.MapModule(new RoutesModule(5, "tests", "tools"));

		Run(sut, ["t"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a command whose words end before a later ambiguous word still weighs what it matched, whatever the registration order: `al be r` can mean the module's `alpha beta`, which outweighs routes that match it only through parameters.")]
	[DataRow(false, DisplayName = "App route registered first")]
	[DataRow(true, DisplayName = "Module route registered first")]
	public void When_ACommandEndsBeforeALaterAmbiguousWord_Then_ItsWordsStillCount(bool moduleFirst)
	{
		var sut = CreateApp();
		if (moduleFirst)
		{
			sut.MapModule(new RoutesModule(5, "alpha beta"));
			sut.Map("alps", () => "ok");
		}
		else
		{
			sut.Map("alps", () => "ok");
			sut.MapModule(new RoutesModule(5, "alpha beta"));
		}

		sut.MapModule(new RoutesModule(7, "{p} {q} run", "{p} {q} report"));

		Run(sut, ["al", "be", "r"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a command an abbreviation could reach through one of its aliases counts as one of its expansions: `g` could mean module A's `run`, aliased `go`, or module B's `gather`, so the two modules tie and the application decides.")]
	public void When_AnAbbreviationCouldMeanAnAliasedCommand_Then_ThatCommandCounts()
	{
		var sut = CreateApp();
		sut.MapModule(new AliasedModule());
		sut.MapModule(new RoutesModule(7, "gather"));

		Run(sut, ["g"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies a hidden command whose word is exactly the ambiguous abbreviation does not tie with what it could expand to: routing never offers the hidden word, so neither does attribution.")]
	public void When_AHiddenCommandIsTheAmbiguousWordItself_Then_TheModuleCodeStillApplies()
	{
		var sut = CreateApp();
		sut.Map("al", () => "ok").Hidden();
		sut.MapModule(new RoutesModule(5, "alpha", "alps"));

		Run(sut, ["al"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a framework failure names no route even when it ended a resolved module command: an input validator throws and reporting that fails too, which is the framework's failure, not the command's.")]
	public void When_AResolvedCommandEndsInAFrameworkError_Then_NoRouteIsNamed()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options =>
		{
			options.Output.AddTransformer("broken", new BrokenTransformer());
			options.Output.DefaultFormat = "broken";
			options.ExitCodes.Resolver = outcome =>
			{
				observed = outcome;
				return outcome.ExitCode;
			};
		});
		sut.UseInputValidator((Func<ParsedInputContext, InputValidationResult>)(_ => throw new InvalidOperationException("validator failed")));
		sut.MapModule(new RoutesModule(5, "tests run"));

		_ = Run(sut, ["tests", "run"]);

		observed.Should().NotBeNull();
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.FrameworkError);
		observed.Route.Should().BeNull();
	}

	[TestMethod]
	[Description("Verifies a refusal is attributed against the routing snapshot that refused the invocation: a banner that withdraws the module once routing has run does not take the refusal away from it.")]
	public void When_AModuleIsWithdrawnAfterRouting_Then_TheRefusalKeepsItsOwner()
	{
		var present = true;
		var sut = CreateApp(static options => options.Output.BannerEnabled = true);
		sut.WithBanner(() =>
		{
			present = false;
			sut.Core.InvalidateRouting();
			return "banner";
		});
		sut.MapModule(new RoutesModule(5, "tests run"), _ => present);

		Run(sut, ["tests", "bogus"]).Should().Be(5);
		present.Should().BeFalse("the banner, which withdraws the module, ran between routing and attribution");
	}

	[TestMethod]
	[Description("Verifies a context and a route that the abbreviation reaches through different expansions tie when they match as many words: a route outranks a context only within one expansion, so the two modules settle as tied modules do.")]
	[DataRow(new[] { "t" }, new[] { "tests" }, new[] { "tools" }, DisplayName = "One word")]
	[DataRow(new[] { "t", "be" }, new[] { "tests beta" }, new[] { "tools beta" }, DisplayName = "Two words")]
	public void When_AContextAndARouteTieAcrossExpansions_Then_TheirModulesTie(string[] args, string[] contexts, string[] routes)
	{
		var sut = CreateApp();
		sut.MapModule(new ContextsModule(5, contexts));
		sut.MapModule(new RoutesModule(7, routes));

		Run(sut, args).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies a route still outranks a context that matches as many words within the same expansion, as for an unknown command.")]
	public void When_AContextAndARouteTieWithinAnExpansion_Then_TheRouteDecides()
	{
		var sut = CreateApp();
		sut.MapModule(new ContextsModule(5, "tests run"));
		sut.MapModule(new RoutesModule(7, "tests run", "tools run"));

		Run(sut, ["t", "run"]).Should().Be(7);
	}

	[TestMethod]
	[Description("Verifies the commands of a hidden context are not something an abbreviation could expand to either: prefix resolution never offers them until the context's words are typed in full.")]
	public void When_AHiddenContextStartsLikeTheModulesCommands_Then_TheModuleCodeStillApplies()
	{
		var sut = CreateApp();
		sut.Context("trace", static trace => trace.Map("dump", () => "ok")).Hidden();
		sut.MapModule(new RoutesModule(5, "tests", "tools"));

		Run(sut, ["t"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a later word is read along each expansion, as prefix resolution reads it: under `tools`, `run` abbreviates `runner` even though `tests run` exists, so the two modules tie and their common ancestor, the application, decides.")]
	public void When_ALaterWordIsExactInOneExpansionAndAnAbbreviationInAnother_Then_BothExpansionsCount()
	{
		var sut = CreateApp();
		sut.MapModule(new RoutesModule(5, "tests run"));
		sut.MapModule(new RoutesModule(7, "tools runner"));

		Run(sut, ["t", "run"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies a word after the ambiguous one that is itself a command word stands for that word only, as prefix resolution reads it: `test` is not an abbreviation of `tests` when `test` exists.")]
	public void When_AWordAfterTheAmbiguousOneIsACommandWord_Then_ItDoesNotAbbreviateLongerWords()
	{
		var sut = CreateApp();
		sut.Map("tools test", () => "ok");
		sut.Map("tasks list", () => "ok");
		sut.MapModule(new RoutesModule(5, "tools tests run"));

		Run(sut, ["t", "test", "run"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies attributing an abbreviation ambiguous at every word stays cheap: routes whose parameters keep both literals reachable at each position must not make the attribution visit every combination of expansions.")]
	public async Task When_EveryWordIsAnAmbiguousAbbreviation_Then_TheRefusalIsAttributedPromptly()
	{
		const int Words = 24;
		var sut = CreateApp();
		for (var position = 0; position < Words; position++)
		{
			foreach (var literal in (string[])["alpha", "alpine"])
			{
				var current = position;
				sut.Map(
					string.Join(' ', Enumerable.Range(0, Words).Select(index => index == current ? literal : $"{{p{index}}}")),
					() => "ok");
			}
		}

		var exitCode = await Task.Run(() => Run(sut, [.. Enumerable.Repeat("al", Words)]))
			.WaitAsync(TimeSpan.FromSeconds(10));

		exitCode.Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies an argument that fails a route constraint, or is missing, is refused with the code of the module whose command it addressed.")]
	public void When_AModuleCommandArgumentIsRefused_Then_TheModuleCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new TestsModule());

		Run(sut, ["tests", "show", "abc"]).Should().Be(5, "the argument fails the route constraint");
		Run(sut, ["tests", "show"]).Should().Be(5, "the argument is missing");
	}

	[TestMethod]
	[Description("Verifies a module mapped inside another inherits its codes, that a code neither declares falls back to the app's, and that the routes a context encloses outrank the context in deciding who owns an unknown command.")]
	public void When_AModuleIsMappedInsideAnother_Then_ItInheritsAndFallsBackToTheApp()
	{
		var sut = CreateApp(options => options.ExitCodes.HandlerError = 9);
		sut.MapModule(new ParentModule());

		Run(sut, ["parent", "child", "bogus"]).Should().Be(6, "the child's words are its own");
		Run(sut, ["parent", "bogus"]).Should().Be(5, "the parent's and the child's routes tie, and resolve to the parent they share");
		Run(sut, ["parent", "child", "check"]).Should().Be(2, "the child inherits the parent's completed-with-failures code");
		Run(sut, ["parent", "plain", "check"]).Should().Be(2, "a module that declares nothing inherits from the one that mapped it");
		Run(sut, ["parent", "child", "fail"]).Should().Be(9, "neither module declares it, so the app's code applies");
	}

	[TestMethod]
	[Description("Verifies a module mounted under one of the app's contexts still owns an unknown command under its words: the context encloses the module's routes.")]
	public void When_AModuleIsMountedUnderAnAppContext_Then_ItOwnsUnknownCommandsThere()
	{
		var sut = CreateApp();
		sut.Context("tests", scoped => scoped.MapModule(new RunOnlyModule()));

		Run(sut, ["tests", "bogus"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies an application route that accepts the first word only as a parameter, or fails on it, does not take an unknown command away from the module whose literal words it starts with.")]
	[DataRow("{id:int}", DisplayName = "A root route whose constraint fails on the first word")]
	[DataRow("{env} {region} deploy", DisplayName = "A root route whose parameters accept the first words")]
	public void When_AnAppRouteOnlyAcceptsTheWordsAsParameters_Then_TheModuleStillOwnsThem(string appRoute)
	{
		var sut = CreateApp();
		sut.Map(appRoute, () => "ok");
		sut.MapModule(new TestsModule());

		Run(sut, ["tests", "bogus"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies a route whose arguments failed does not take a refusal away from a context matching more of the literal words: the context's module decides, and no route is named.")]
	[DataRow("tests {count:int}", new[] { "tests", "bad", "extra" }, DisplayName = "Route constraint failure")]
	[DataRow("tests {count} {id}", new[] { "tests", "bad" }, DisplayName = "Missing route argument")]
	public void When_AFailedRouteMatchesFewerLiteralWordsThanAContext_Then_TheContextModuleDecides(string failedRoute, string[] args)
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, failedRoute));
		sut.MapModule(new BadGroupModule());

		Run(sut, args).Should().Be(7);
		observed.Should().NotBeNull();
		observed!.Route.Should().BeNull();
	}

	[TestMethod]
	[Description("Verifies a route whose argument failed keeps the refusal when a context matches as many literal words: its module decides, and the route names what the invocation addressed.")]
	public void When_AFailedRouteTiesAContext_Then_TheRouteKeepsTheRefusal()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "tests bad {count:int}"));
		sut.MapModule(new BadGroupModule());

		Run(sut, ["tests", "bad", "x"]).Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("tests bad {count:int}");
	}

	[TestMethod]
	[Description("Verifies a context that encloses no matching route still decides who owns an unknown command under its words.")]
	public void When_OnlyAContextMatchesTheWords_Then_ItsModuleOwnsThem()
	{
		var sut = CreateApp();
		sut.MapModule(new GuardedModule());

		Run(sut, ["empty", "bogus"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies modules that share words and apply the same codes need no arbiter: an unknown command there takes their common code.")]
	public void When_ModulesShareTheWordsAndTheirCodes_Then_TheirCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new SharedWordsModule("x", usageError: 5));
		sut.MapModule(new SharedWordsModule("y", usageError: 5));

		Run(sut, ["tools", "bogus"]).Should().Be(5);
	}

	[TestMethod]
	[Description("Verifies an unknown command shared by the words of two unrelated modules falls back to the app's code rather than picking one of them.")]
	public void When_TwoUnrelatedModulesShareTheWords_Then_TheAppCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new SharedWordsModule("x", usageError: 5));
		sut.MapModule(new SharedWordsModule("y", usageError: 7));

		Run(sut, ["tools", "bogus"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies an unknown output format given to a module command is that command's refusal: it is reported when the command renders, so it takes the module's code.")]
	public void When_AModuleCommandIsGivenAnUnknownOutputFormat_Then_TheModuleCodeApplies()
	{
		var sut = CreateApp();
		sut.MapModule(new TestsModule());
		sut.Map("other run", () => "ok");

		Run(sut, ["tests", "rerun", "--output:bogus"]).Should().Be(5);
		Run(sut, ["other", "run", "--output:bogus"]).Should().Be(2);
	}

	[TestMethod]
	[Description("Verifies a hosted protocol-passthrough command that cannot run hosted, given an unknown output format, is refused as its module would refuse it and names its route, as any other resolved module command does; with a valid format, the hosting failure keeps the application's code and names no route.")]
	public void When_AHostedPassthroughCommandIsGivenAnUnknownOutputFormat_Then_TheModuleCodeApplies()
	{
		var observed = new List<ReplExecutionOutcome>();
		var sut = CreateApp(options =>
		{
			options.ExitCodes.FrameworkError = 70;
			options.ExitCodes.Resolver = outcome =>
			{
				observed.Add(outcome);
				return outcome.ExitCode;
			};
		});
		sut.MapModule(new ServeModule());

		Run(sut, ["tests", "serve", "--output:bogus"]).Should().Be(5);
		Run(sut, ["tests", "serve"]).Should().Be(70);
		observed.Select(static outcome => outcome.Route).Should().Equal("tests serve", null);
	}

	[TestMethod]
	[Description("Verifies a context's failed validation belongs to the module that mapped the context, unless the invocation resolved to a command, whose module then decides.")]
	public void When_AContextValidationFails_Then_TheContextOrTheCommandModuleDecides()
	{
		var sut = CreateApp();
		sut.MapModule(new GuardedModule());
		sut.Context("tests", scoped => scoped.MapModule(new RunOnlyModule()), validation: () => false);

		Run(sut, ["guarded"]).Should().Be(5, "the module mapped the context");
		Run(sut, ["tests"]).Should().Be(2, "the application mapped the context");
		Run(sut, ["tests", "run"]).Should().Be(5, "the invocation resolved to the module's command");
	}

	[TestMethod]
	[Description("Verifies the resolver receives the module's code and the route that produced the outcome.")]
	public void When_AResolverIsSet_Then_ItSeesTheModuleCodeAndTheRoute()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new TestsModule());

		_ = Run(sut, ["tests", "run"]);

		observed.Should().NotBeNull();
		observed!.ExitCode.Should().Be(2);
		observed.Route.Should().Be("tests run");
	}

	[TestMethod]
	[Description("Verifies a refused argument still names the route it addressed, and that an outcome no command produced carries none.")]
	public void When_AnInvocationIsRefused_Then_TheRouteNamesWhatItAddressed()
	{
		var observed = new List<ReplExecutionOutcome>();
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed.Add(outcome);
			return outcome.ExitCode;
		});
		sut.MapModule(new TestsModule());

		_ = Run(sut, ["tests", "show", "abc"]);
		_ = Run(sut, ["bogus"]);

		observed.Select(static outcome => outcome.Route).Should().Equal("tests show {id:int}", null);
	}

	[TestMethod]
	[Description("Verifies which kinds a module can override and which it cannot, so a new outcome kind has to be placed on one side or the other, and that a module that declares nothing inherits every overridable code.")]
	public void When_EveryKindIsOverridden_Then_OnlyTheCommandOutcomesTakeAModuleCode()
	{
		var overrides = new ReplExitCodeOverrides
		{
			Success = 1,
			UsageError = 1,
			BindingError = 1,
			HandlerError = 1,
			HandlerException = 1,
			CompletedWithFailures = 1,
		};
		ReplExecutionOutcomeKind[] overridable =
		[
			ReplExecutionOutcomeKind.Success,
			ReplExecutionOutcomeKind.UsageError,
			ReplExecutionOutcomeKind.BindingError,
			ReplExecutionOutcomeKind.HandlerError,
			ReplExecutionOutcomeKind.HandlerException,
			ReplExecutionOutcomeKind.CompletedWithFailures,
		];

		ReplExecutionOutcomeKind[] runLevel =
		[
			ReplExecutionOutcomeKind.Help,
			ReplExecutionOutcomeKind.HandlerExitCode,
			ReplExecutionOutcomeKind.Cancelled,
			ReplExecutionOutcomeKind.Interrupted,
			ReplExecutionOutcomeKind.FrameworkError,
		];
		var inherited = new ReplExitCodeOverrides().InheritFrom(overrides);

		overridable.Concat(runLevel).Should().BeEquivalentTo(
			Enum.GetValues<ReplExecutionOutcomeKind>(),
			"a new kind must be declared overridable or not before it can ship");
		Enum.GetValues<ReplExecutionOutcomeKind>()
			.Where(kind => overrides.For(kind) is not null)
			.Should().BeEquivalentTo(overridable);
		overridable.Should().OnlyContain(kind => inherited.For(kind) == 1, "an unset code is inherited");
	}

	private static ReplApp CreateApp(Action<ReplOptions>? configure = null)
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
			configure?.Invoke(options);
		});
		return app;
	}

	private static int Run(ReplApp sut, string[] args)
	{
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
		return sut.Run(args);
	}

	private sealed class TestsModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5, CompletedWithFailures = 2 };

		public void Map(IReplMap map)
		{
			map.Map("tests run", () => Results.CompletedWithFailures("1 failed"));
			map.Map("tests rerun", () => "ok");
			map.Map("tests show {id:int}", (int id) => id);
		}
	}

	private sealed class EveryKindModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new()
		{
			Success = 10,
			UsageError = 11,
			BindingError = 12,
			HandlerError = 13,
			HandlerException = 14,
			CompletedWithFailures = 15,
		};

		public void Map(IReplMap map)
		{
			map.Map("kinds succeed", () => "ok");
			map.Map("kinds bind", ([FromServices] IUnregisteredService service) => service.ToString());
			map.Map("kinds fail", () => Results.Error("failed", "It failed."));
			map.Map("kinds throw", (Func<string>)(() => throw new InvalidOperationException("boom")));
			map.Map("kinds report", () => Results.CompletedWithFailures("1 failed"));
			map.Map("kinds exit", () => Results.Exit(0));
		}
	}

	private sealed class ParentModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5, CompletedWithFailures = 2 };

		public void Map(IReplMap map)
		{
			map.Map("parent ping", () => "pong");
			map.Context("parent", scoped =>
			{
				scoped.MapModule(new ChildModule());
				scoped.MapModule(new PlainModule());
			});
		}
	}

	private sealed class ChildModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 6 };

		public void Map(IReplMap map)
		{
			map.Map("child check", () => Results.CompletedWithFailures("1 failed"));
			map.Map("child fail", () => Results.Error("failed", "It failed."));
		}
	}

	private sealed class PlainModule : IReplModule
	{
		public void Map(IReplMap map) => map.Map("plain check", () => Results.CompletedWithFailures("1 failed"));
	}

	private sealed class GuardedModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5 };

		public void Map(IReplMap map)
		{
			map.Context("guarded", scoped => scoped.Map("run", () => "ok"), validation: () => false);
			map.Context("empty", static _ => { });
		}
	}

	private sealed class RunOnlyModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5 };

		public void Map(IReplMap map) => map.Map("run", () => "ok");
	}

	private sealed class ToolingModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5 };

		public void Map(IReplMap map)
		{
			map.Map("tests run", () => "ok");
			map.Map("tests report", () => "ok");
			map.Map("tools run", () => "ok");
		}
	}

	private sealed class BrokenTransformer : IOutputTransformer
	{
		public string Name => "broken";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("transformer is broken");
	}

	private sealed class ContextsModule(int usageError, params string[] templates) : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = usageError };

		public void Map(IReplMap map)
		{
			foreach (var template in templates)
			{
				map.Context(template, static _ => { });
			}
		}
	}

	private sealed class AliasedModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5 };

		public void Map(IReplMap map) => map.Map("run", () => "ok").WithAlias("go");
	}

	private sealed class RoutesModule(int usageError, params string[] templates) : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = usageError };

		public void Map(IReplMap map)
		{
			foreach (var template in templates)
			{
				map.Map(template, () => "ok");
			}
		}
	}

	// No IReplIoContext parameter, so a hosted session refuses to run it.
	private sealed class ServeModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 5 };

		public void Map(IReplMap map) => map.Map("tests serve", () => Results.Exit(0)).AsProtocolPassthrough();
	}

	private sealed class BadGroupModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 7 };

		public void Map(IReplMap map) => map.Context("tests bad group", static _ => { });
	}

	private sealed class SharedWordsModule(string command, int usageError) : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = usageError };

		public void Map(IReplMap map) => map.Map($"tools {command}", () => "ok");
	}

	public interface IUnregisteredService;
}
