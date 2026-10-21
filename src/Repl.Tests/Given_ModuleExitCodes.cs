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
	[Description("Verifies an input validator of a resolved module command that throws is that command's handler failure even when its report degrades to unformatted text, the caller having still seen it: it names the command's route.")]
	public void When_AResolvedCommandsValidatorThrowsAndItsReportDegrades_Then_ItIsTheCommandsHandlerFailure()
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
		observed!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		observed.Route.Should().Be("tests run");
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
	[Description("Verifies an application route that accepts the first word only as a parameter, or fails on it, does not take an unknown command away from the module whose literal words it starts with, and the refusal says so.")]
	[DataRow("{id:int}", DisplayName = "A root route whose constraint fails on the first word")]
	[DataRow("{env} {region} deploy", DisplayName = "A root route whose parameters accept the first words")]
	public void When_AnAppRouteOnlyAcceptsTheWordsAsParameters_Then_TheModuleStillOwnsThem(string appRoute)
	{
		var sut = CreateApp();
		sut.Map(appRoute, () => "ok");
		sut.MapModule(new TestsModule());

		var (exitCode, output) = RunCapturing(sut, ["tests", "bogus"]);

		exitCode.Should().Be(5);
		output.Should().Contain("Unknown command 'tests bogus'");
	}

	[TestMethod]
	[Description("Verifies a route whose arguments failed does not take a refusal away from a context matching more of the literal words: the context's module decides, no route is named, and the refusal is an unknown command among those words rather than the failed route's argument error.")]
	[DataRow("tests {count:int}", "tests bad extra", "Invalid value", DisplayName = "Route constraint failure")]
	[DataRow("tests {count} {id}", "tests bad", "Missing value", DisplayName = "Missing route argument")]
	public void When_AFailedRouteMatchesFewerLiteralWordsThanAContext_Then_TheContextModuleDecides(string failedRoute, string input, string argumentError)
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, failedRoute));
		sut.MapModule(new BadGroupModule());

		var (exitCode, output) = RunCapturing(sut, input.Split(' '));

		exitCode.Should().Be(7);
		observed.Should().NotBeNull();
		observed!.Route.Should().BeNull();
		output.Should().Contain($"Unknown command '{input}'");
		output.Should().NotContain(argumentError);
	}

	[TestMethod]
	[Description("Verifies the refusal of a failed route outscored by a context is an unknown command for a program too: its JSON carries the unknown-command code.")]
	public void When_AFailedRouteMatchesFewerLiteralWordsThanAContextUnderJson_Then_TheRefusalIsAnUnknownCommand()
	{
		var sut = CreateApp();
		sut.MapModule(new RoutesModule(5, "tests {count:int}"));
		sut.MapModule(new BadGroupModule());

		var (exitCode, output) = RunCapturing(sut, ["tests", "bad", "extra", "--json"]);

		exitCode.Should().Be(7);
		using var document = System.Text.Json.JsonDocument.Parse(output);
		document.RootElement.GetProperty("code").GetString().Should().Be("unknown_command");
	}

	[TestMethod]
	[Description("Verifies a route whose argument failed does not keep the refusal when another route matches more of the literal words: the invocation is an unknown command among them, with that route suggested.")]
	public void When_AFailedRouteMatchesFewerLiteralWordsThanAnotherRoute_Then_TheRefusalIsAnUnknownCommand()
	{
		var sut = CreateApp();
		sut.MapModule(new RoutesModule(5, "tests {count:int}", "tests bad group"));

		var (exitCode, output) = RunCapturing(sut, ["tests", "bad", "extra"]);

		exitCode.Should().Be(5);
		output.Should().Contain("Unknown command 'tests bad extra'. Did you mean 'tests bad group'?");
	}

	[TestMethod]
	[Description("Verifies a parameter that rejects the word typed at its position still addresses it, whatever follows: a sibling route whose parameter accepts that word only ties it, so the refusal stays the argument's error rather than an unknown command for the start of a real command.")]
	[DataRow("user bob", DisplayName = "The words the failed route has room for")]
	[DataRow("user bob --force", DisplayName = "An option of the failed route after them")]
	[DataRow("user bob rnme x", DisplayName = "Words neither route matches after them")]
	public void When_AFailedParameterTiesASiblingThatAcceptsTheWord_Then_TheArgumentErrorStands(string input)
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "user {id:int}", "user {name} rename {newName}"));

		var (exitCode, output) = RunCapturing(sut, input.Split(' '));

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("user {id:int}");
		output.Should().Contain("Invalid value 'bob' for parameter 'id'");
	}

	[TestMethod]
	[Description("Verifies a command typed inside a context whose parameter accepted a word is not refused for that word by a route rejecting it: the scope's words are not the line's, and the line is an unknown command in that context.")]
	public void When_AnUnknownCommandIsTypedInsideAContext_Then_ARouteRejectingTheScopeWordDoesNotTakeTheRefusal()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.BannerEnabled = false);
		sut.MapModule(new RoutesModule(5, "project {id:int}"));
		sut.MapModule(new ProjectContextModule());
		using var writer = new StringWriter();
		using var reader = new StringReader("project foo\nbogus\nexit\n");
		using var session = ReplSessionIO.SetSession(writer, reader);

		_ = sut.Run([]);

		writer.ToString().Should().Contain("Unknown command 'project foo bogus'");
		writer.ToString().Should().NotContain("Invalid value 'foo'");
	}

	[TestMethod]
	[Description("Verifies the same words typed on one line, scope included, are a tie between the route rejecting the context's word and the context: the refusal stays that argument's error, since the user typed that word.")]
	public void When_TheContextWordsAndAnUnknownCommandAreTypedOnOneLine_Then_TheRouteRejectingTheWordKeepsTheRefusal()
	{
		var sut = CreateApp();
		sut.MapModule(new RoutesModule(5, "project {id:int}"));
		sut.MapModule(new ProjectContextModule());

		var (exitCode, output) = RunCapturing(sut, ["project", "foo", "bogus"]);

		exitCode.Should().Be(5);
		output.Should().Contain("Invalid value 'foo' for parameter 'id'");
	}

	[TestMethod]
	[Description("Verifies a route rejecting a word of the scope never takes the line's refusal, even when it spells out more literal words than the context the session entered.")]
	public void When_ARouteRejectingAScopeWordHasMoreLiteralWordsThanTheContext_Then_TheLineIsStillAnUnknownCommand()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.BannerEnabled = false);
		sut.MapModule(new RoutesModule(5, "repo dotnet {id:int}"));
		sut.MapModule(new RepoContextModule());
		using var writer = new StringWriter();
		using var reader = new StringReader("repo dotnet runtime\nbogus\nexit\n");
		using var session = ReplSessionIO.SetSession(writer, reader);

		_ = sut.Run([]);

		writer.ToString().Should().Contain("Unknown command 'repo dotnet runtime bogus'");
		writer.ToString().Should().NotContain("Invalid value 'runtime'");
	}

	[TestMethod]
	[Description("Verifies the scope rule weighs the words routing actually read: inside a context, a hook replacing the line with a native command leaves no scope before it, so that command's failed argument is still its error.")]
	public void When_AHookInsideAContextReplacesTheLineWithANativeCommand_Then_ItsFailedArgumentIsItsError()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.BannerEnabled = false);
		sut.MapModule(new RoutesModule(5, "user {id:int} show"));
		sut.MapModule(new ProjectContextModule());
		sut.UseInvocationHook((ReplInvocationContext context) => context.Request.Arguments is ["bogus"]
			? ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand(
				"user {id:int} show", new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "abc" }))
			: ReplInvocationResult.Continue());
		using var writer = new StringWriter();
		using var reader = new StringReader("project foo\nbogus\nexit\n");
		using var session = ReplSessionIO.SetSession(writer, reader);

		_ = sut.Run([]);

		writer.ToString().Should().Contain("Invalid value 'abc' for parameter 'id'");
	}

	[TestMethod]
	[Description("Verifies the refusal weighs every route whose argument failed, not only the failure routing ranked first on its own scale: a route matching more literal words, whose own constraint failed, keeps the refusal with that argument's error rather than an unknown command.")]
	public void When_TheRankedFailureIsOutscoredByAnotherFailedRoute_Then_ThatRoutesArgumentErrorStands()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "cmd {a:int} {b:int} {c:int} {d:int} {e:int} {bad:int}", "cmd 1 {bad:alpha}"));

		var (exitCode, output) = RunCapturing(sut, ["cmd", "1", "2", "3", "4", "5", "x"]);

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("cmd 1 {bad:alpha}");
		output.Should().Contain("Invalid value '2' for parameter 'bad'");
	}

	[TestMethod]
	[Description("Verifies an addressed failed constraint comes before a missing argument, whichever failure routing ranked first: a route rejecting a word it has room for is a sharper refusal than another route waiting for more.")]
	public void When_AnAddressedConstraintFailureAndAMissingArgumentCompete_Then_TheConstraintFailureStands()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "t {p} {q} {r} {s}", "t {x} {y} {z:int}", "t {a:int} {b:bool}"));

		var (exitCode, output) = RunCapturing(sut, ["t", "1", "2", "x"]);

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("t {x} {y} {z:int}");
		output.Should().Contain("Invalid value 'x' for parameter 'z'");
	}

	[TestMethod]
	[Description("Verifies the choice among several addressed failed routes is the same whichever of them was mapped first.")]
	[DataRow(false, DisplayName = "Shorter route mapped first")]
	[DataRow(true, DisplayName = "Longer route mapped first")]
	public void When_SeveralFailedRoutesAreAddressed_Then_TheMappingOrderDoesNotChangeTheChoice(bool longerFirst)
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		string[] addressed = longerFirst
			? ["cmd 1 {a:int} {bad:alpha}", "cmd 1 {bad:alpha}"]
			: ["cmd 1 {bad:alpha}", "cmd 1 {a:int} {bad:alpha}"];
		sut.MapModule(new RoutesModule(5, ["cmd {a:int} {b:int} {c:int} {d:int} {e:int} {f:int} {bad:int}", .. addressed]));

		var (exitCode, output) = RunCapturing(sut, ["cmd", "1", "2", "3", "4", "5", "6", "x"]);

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("cmd 1 {a:int} {bad:alpha}");
		output.Should().Contain("Invalid value '3' for parameter 'bad'");
	}

	[TestMethod]
	[Description("Verifies that when a route's failed constraint is outscored but the route missing an argument matches as many words as any owner, the refusal is that missing argument rather than an unknown command.")]
	public void When_AConstraintFailureIsOutscoredButAMissingArgumentIsNot_Then_TheMissingArgumentIsReported()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "tests {count:int}", "tests bad {name}"));

		var (exitCode, output) = RunCapturing(sut, ["tests", "bad"]);

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("tests bad {name}");
		output.Should().Contain("Missing value for parameter 'name'");
	}

	[TestMethod]
	[Description("Verifies an interactive session refuses a route whose argument failed, when a context matches more of the words typed, as an unknown command, as a one-shot run does.")]
	public void When_AFailedRouteMatchesFewerLiteralWordsThanAContextInteractively_Then_TheRefusalIsAnUnknownCommand()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.Output.BannerEnabled = false);
		sut.MapModule(new RoutesModule(5, "tests {count:int}"));
		sut.MapModule(new BadGroupModule());
		using var writer = new StringWriter();
		using var reader = new StringReader("tests bad extra\nexit\n");
		using var session = ReplSessionIO.SetSession(writer, reader);

		_ = sut.Run([]);

		writer.ToString().Should().Contain("Unknown command 'tests bad extra'");
		writer.ToString().Should().NotContain("Invalid value");
	}

	[TestMethod]
	[Description("Verifies a route whose argument failed keeps the refusal when a context matches as many literal words: its module decides, the route names what the invocation addressed, and the message is the argument's error.")]
	public void When_AFailedRouteMatchesAsManyLiteralWordsAsAContext_Then_TheRouteKeepsTheRefusal()
	{
		ReplExecutionOutcome? observed = null;
		var sut = CreateApp(options => options.ExitCodes.Resolver = outcome =>
		{
			observed = outcome;
			return outcome.ExitCode;
		});
		sut.MapModule(new RoutesModule(5, "tests bad {count:int}"));
		sut.MapModule(new BadGroupModule());

		var (exitCode, output) = RunCapturing(sut, ["tests", "bad", "x"]);

		exitCode.Should().Be(5);
		observed.Should().NotBeNull();
		observed!.Route.Should().Be("tests bad {count:int}");
		output.Should().Contain("Invalid value 'x' for parameter 'count'");
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

	private static int Run(ReplApp sut, string[] args) => RunCapturing(sut, args).ExitCode;

	private static (int ExitCode, string Output) RunCapturing(ReplApp sut, string[] args)
	{
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
		var exitCode = sut.Run(args);
		return (exitCode, writer.ToString());
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

	private sealed class RepoContextModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 7 };

		public void Map(IReplMap map) => map.Context("repo {owner} {name}", static repo => repo.Map("clone", () => "cloned"));
	}

	private sealed class ProjectContextModule : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = 7 };

		public void Map(IReplMap map) => map.Context("project {name}", static project => project.Map("build", () => "built"));
	}

	private sealed class SharedWordsModule(string command, int usageError) : IReplModuleExitCodes
	{
		public ReplExitCodeOverrides ExitCodes { get; } = new() { UsageError = usageError };

		public void Map(IReplMap map) => map.Map($"tools {command}", () => "ok");
	}

	public interface IUnregisteredService;
}
