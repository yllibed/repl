namespace Repl.Tests;

[TestClass]
public sealed class Given_InteractiveAutocomplete_OptionCandidates
{
	[TestMethod]
	[Description("Interactive autocomplete suggests route and global option names when the current token is an option prefix.")]
	public async Task When_CurrentTokenIsOptionPrefix_Then_SuggestsRouteAndGlobalOptions()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--org"]));
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--force");
		values.Should().Contain("--help");
		values.Should().Contain("--json");
		values.Should().Contain("--tenant");
		values.Should().Contain("--org");
		result.Suggestions.Should().OnlyContain(static suggestion => suggestion.IsSelectable);
		result.HintLine.Should().Contain("--force");
	}

	[TestMethod]
	[Description("Interactive autocomplete filters option suggestions by a partial option prefix.")]
	public async Task When_CurrentTokenIsPartialOptionPrefix_Then_FiltersOptionSuggestions()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls --fo").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().ContainSingle().Which.Should().Be("--force");
		result.ReplaceStart.Should().Be("install bib-overalls ".Length);
		result.ReplaceLength.Should().Be("--fo".Length);
		result.HintLine.Should().Be("--force");
	}

	[TestMethod]
	[Description("A valued option given in its space-separated form must not break route matching: the option's VALUE is consumed like the invocation parser does, so '--channel beta' does not leave a stray positional that hides the route's own options.")]
	public async Task When_ValuedOptionPrecedesOptionPrefix_Then_RouteOptionsAreStillSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
				"install {skillName}",
				static string (string skillName, [ReplOption] bool force, [ReplOption] string? channel) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls --channel beta --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--force", because: "consuming '--channel beta' leaves the same command prefix the router sees");
	}

	[TestMethod]
	[Description("Command aliases resolve for option suggestions like they do for execution: a command whose TERMINAL literal is aliased ('ls' for 'list') surfaces the route's options when invoked through the alias, exercising RouteResolver's terminal-segment alias matching (not unique-prefix expansion).")]
	public async Task When_CommandIsInvokedThroughAlias_Then_RouteOptionsAreSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact list", static string ([ReplOption] bool verbose) => verbose.ToString())
			.WithAlias("ls")
			.WithDescription("List contacts.");

		var result = await ResolveAutocompleteAsync(sut, "contact ls --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--verbose", because: "the router accepts the terminal-segment alias, so autocomplete must too");
	}

	[TestMethod]
	[Description("Unique command-prefix expansion feeds option suggestions like it feeds execution: typing a unique prefix of a command ('i' for 'install') resolves the route and surfaces its options.")]
	public async Task When_CommandIsInvokedThroughUniquePrefix_Then_RouteOptionsAreSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "i bib-overalls --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--force", because: "'i' uniquely prefixes 'install', so its route options must appear");
	}

	[TestMethod]
	[Description("A single dash is already an option prefix: short option aliases declared via [ReplOption(Aliases = [\"-f\"])] must be suggested when the user types '-'.")]
	public async Task When_CurrentTokenIsSingleDash_Then_ShortOptionAliasesAreSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
				"install {skillName}",
				static string (string skillName, [ReplOption(Aliases = ["-f"])] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls -").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("-f", because: "declared short aliases are reachable only from a single-dash prefix");
		values.Should().Contain("--force");
	}

	[TestMethod]
	[Description("Route options wait until required positionals are filled: 'install --' (with {skillName} unfilled) must NOT offer --force, because accepting it yields 'install --force', which does not run cleanly. Global options still appear.")]
	public async Task When_RequiredPositionalIsStillMissing_Then_RouteOptionsAreNotSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("--force", because: "a required positional is unfilled — 'install --force' does not run cleanly, so route options wait until the arguments are satisfied");
		values.Should().Contain("--help", because: "global options remain available regardless of positional state");
	}

	[TestMethod]
	[Description("A bare '--' prior token is the POSIX end-of-options separator: everything after it is positional, so no option name may be suggested past it.")]
	public async Task When_EndOfOptionsSeparatorPrecedes_Then_NoOptionIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls -- --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("--force", because: "tokens after the end-of-options separator are positional");
		values.Should().NotContain("--help");
	}

	[TestMethod]
	[Description("A signed numeric literal is a positional argument, not an option prefix: typing '-4' must not open the option menu (mirrors the invocation parser's IsSignedNumericLiteral rule).")]
	public async Task When_CurrentTokenIsNegativeNumber_Then_NoOptionIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls -4").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("--force");
	}

	[TestMethod]
	[Description("Dynamic completion providers complete parameter VALUES; when the current token is an option prefix the provider must not run, otherwise its values pollute the option menu.")]
	public async Task When_CurrentTokenIsOptionPrefix_Then_DynamicCompletionProviderIsSkipped()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithCompletion("skillName", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["ga", "bu", "zo", "meu"]))
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("ga", because: "provider values are parameter values, not option names");
		values.Should().Contain("--force");
	}

	[TestMethod]
	[Description("An option name shared by a global option and a route option appears exactly once in the menu — the dedup set spans both sources.")]
	public async Task When_GlobalAndRouteOptionsCollide_Then_SuggestionAppearsOnce()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("force"));
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls --fo").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Count(static value => string.Equals(value, "--force", StringComparison.OrdinalIgnoreCase))
			.Should().Be(1);
	}

	[TestMethod]
	[Description("In option-prefix mode the live hint must describe the option alternatives, not a positional parameter: 'install bib-overalls --' lists --force in the menu, so a 'Param' hint would contradict what the user is looking at.")]
	public async Task When_OptionPrefixOnTerminalRoute_Then_HintShowsOptionsNotParameter()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName} {version}", static string (string skillName, string version, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "install bib-overalls 42 --").ConfigureAwait(false);

		result.HintLine.Should().NotContain("Param", because: "the user asked for options, not a positional parameter");
		result.HintLine.Should().Contain("--force");
	}

	[TestMethod]
	[Description("Autocomplete must NEVER touch the filesystem: a '@file' prior token stays a literal token instead of being expanded as a response file. Hosted sessions feed remote-controlled lines through this path on every keystroke — expansion would mean server-side file reads (UNC probes included) driven by keystrokes.")]
	public async Task When_PriorTokenLooksLikeResponseFile_Then_ItIsNotExpanded()
	{
		var responseFile = Path.GetTempFileName();
		try
		{
			// If the parser expanded this, the prefix would gain three tokens and the
			// route would no longer match — observable as the route options vanishing.
			await File.WriteAllTextAsync(responseFile, "ga bu zo").ConfigureAwait(false);
			var sut = CoreReplApp.Create();
			sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
				.WithDescription("Install a skill.");

			var result = await ResolveAutocompleteAsync(sut, $"install @{responseFile} --").ConfigureAwait(false);

			var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
			values.Should().Contain("--force", because: "the @token must be treated as a plain positional, not expanded from disk");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	[TestMethod]
	[Description("Suggestion/execution parity (issue #45): route resolution binds segments positionally, so in 'deploy -- -f ' the '--' itself fills {target} and '-f' lands in the option region — no provider value typed there could bind at execution, so the provider must NOT fire.")]
	public async Task When_TokenFollowsSeparatorBoundPositional_Then_ProviderDoesNotFire()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithDescription("Deploy a target.");

		var result = await ResolveAutocompleteAsync(sut, "deploy -- -f ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("zo-profile", because: "'--' already fills {target}; a value on the trailing region cannot bind to the parameter");
	}

	[TestMethod]
	[Description("Parity contract between the two completion surfaces: for the same app and the same '--' input, the interactive menu and the shell-completion candidates carry the same option tokens. The shared token source is only half the guarantee — the gates must agree too, and this pin catches a divergent reimplementation on either side.")]
	public async Task When_ComparingInteractiveAndShellOnDoubleDash_Then_CandidatesMatch()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant", aliases: ["--org"]));
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string shellLine = "app install bib-overalls --";

		var interactive = await ResolveAutocompleteAsync(sut, "install bib-overalls --").ConfigureAwait(false);
		var shell = shellEngine.ResolveShellCompletionCandidates(shellLine, shellLine.Length);

		var interactiveOptions = interactive.Suggestions
			.Where(static suggestion => suggestion.IsSelectable)
			.Select(static suggestion => suggestion.Value)
			.ToArray();
		interactiveOptions.Should().BeEquivalentTo(shell);
	}

	[TestMethod]
	[Description("Parity contract for the single-dash gate: shell completion must surface short option aliases (-f) from '-' exactly like the interactive menu does — the two surfaces answering the same question differently is operator-visible confusion.")]
	public async Task When_ComparingInteractiveAndShellOnSingleDash_Then_CandidatesMatch()
	{
		var sut = CoreReplApp.Create();
		sut.Map(
				"install {skillName}",
				static string (string skillName, [ReplOption(Aliases = ["-f"])] bool force) => skillName)
			.WithDescription("Install a skill.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string shellLine = "app install bib-overalls -";

		var interactive = await ResolveAutocompleteAsync(sut, "install bib-overalls -").ConfigureAwait(false);
		var shell = shellEngine.ResolveShellCompletionCandidates(shellLine, shellLine.Length);

		var interactiveOptions = interactive.Suggestions
			.Where(static suggestion => suggestion.IsSelectable)
			.Select(static suggestion => suggestion.Value)
			.ToArray();
		interactiveOptions.Should().Contain("-f");
		interactiveOptions.Should().BeEquivalentTo(shell);
	}

	[TestMethod]
	[Description("Overloaded routes select ONE route the way execution does: 'item 42 --' resolves to the int overload, so only its options appear, never the string overload's — accepting a string-only option would fail against the int route that actually runs.")]
	public async Task When_RouteIsOverloaded_Then_OnlyTheSelectedOverloadsOptionsAreSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("item {value}", static string (string value, [ReplOption] bool tag) => value)
			.WithDescription("String item.");
		sut.Map("item {value:int}", static string (int value, [ReplOption] bool verbose) => $"{value}")
			.WithDescription("Int item.");

		var result = await ResolveAutocompleteAsync(sut, "item 42 --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--verbose", because: "42 resolves to the int overload");
		values.Should().NotContain("--tag", because: "the string overload is not the one that would run");
	}

	[TestMethod]
	[Description("A valueless global flag before the command must not swallow the command word: '--no-logo install bib-overalls --' still resolves the install route (GlobalOptionParser knows --no-logo takes no value), so route options appear.")]
	public async Task When_ValuelessGlobalFlagPrecedesCommand_Then_RouteOptionsAreStillSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName, [ReplOption] bool force) => skillName)
			.WithDescription("Install a skill.");

		var result = await ResolveAutocompleteAsync(sut, "--no-logo install bib-overalls --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--force", because: "'--no-logo' is a valueless global and must not consume 'install' as its value");
	}

	[TestMethod]
	[Description("A valued short option alias must not break route resolution: 'run -c beta --' keeps the terminal 'run' route (the router leaves '-c beta' as route-option tokens), so route options keep appearing after the alias value.")]
	public async Task When_ValuedShortAliasPrecedesOptionPrefix_Then_RouteOptionsAreStillSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption(Aliases = ["-c"])] string? channel, [ReplOption] bool force) => channel ?? "none")
			.WithDescription("Run something.");

		var result = await ResolveAutocompleteAsync(sut, "run -c beta --").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("--force", because: "'-c beta' are route-option tokens; the 'run' route stays terminal");
	}

	[TestMethod]
	[Description("Suggestion/execution parity (issue #45): in 'deploy x -- -' the value 'x' already binds {target} and '--' sits in the option region, so the provider must NOT fire for the trailing '-' — accepting a candidate would add a positional that execution rejects.")]
	public async Task When_DashCurrentTokenFollowsSeparatorPastBoundValue_Then_ProviderDoesNotFire()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithDescription("Deploy a target.");

		var result = await ResolveAutocompleteAsync(sut, "deploy x -- -").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().NotContain("zo-profile", because: "{target} is bound to 'x'; nothing typed past '--' can bind to it");
	}

	[TestMethod]
	[Description("Shell parity for a valued short alias: after 'app install pkg -f ' shell completion still offers the install route's options, matching what execution parses ('-f' is a route option, not a stray positional).")]
	public void When_ShellCompletesAfterValuedShortAlias_Then_RouteOptionsAreStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {name}", static string (string name, [ReplOption(Aliases = ["-f"])] bool force) => name)
			.WithDescription("Install a skill.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app install pkg -f --";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().Contain("--force", because: "'-f' is a route option; the install route stays terminal in shell completion too");
	}

	[TestMethod]
	[Description("Shell parity for the POSIX separator: after 'app install pkg -- ' everything is positional, so shell completion offers no option names past '--'.")]
	public void When_ShellCompletesAfterSeparator_Then_NoOptionIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {name}", static string (string name, [ReplOption] bool force) => name)
			.WithDescription("Install a skill.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app install pkg -- --";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().NotContain("--force", because: "tokens after '--' are positional");
		candidates.Should().NotContain("--help");
	}

	[TestMethod]
	[Description("Route resolution runs before option parsing, so a dash-prefixed token can satisfy a required positional: 'remote -prod st' binds '-prod' to {name}, leaving the later literal 'status' as the completion — dropping '-prod' as if it were an option would hide it.")]
	public async Task When_DashPrefixedTokenFillsRequiredPositional_Then_LaterLiteralIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("remote {name} status", static string (string name) => name)
			.WithDescription("Remote status.");

		var result = await ResolveAutocompleteAsync(sut, "remote -prod st").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("status", because: "'-prod' binds to {name}; the next segment's literal must still be suggested");
	}

	[TestMethod]
	[Description("The bare '--' separator is a positional value to route resolution (which runs before option parsing): 'remote -- st' binds '--' to {name}, so the later literal 'status' is still the completion.")]
	public async Task When_SeparatorFillsRequiredPositional_Then_LaterLiteralIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("remote {name} status", static string (string name) => name)
			.WithDescription("Remote status.");

		var result = await ResolveAutocompleteAsync(sut, "remote -- st").ConfigureAwait(false);

		var values = result.Suggestions.Select(static suggestion => suggestion.Value).ToArray();
		values.Should().Contain("status", because: "'--' binds to {name} as a positional; the next literal must still be suggested");
	}

	[TestMethod]
	[Description("Per-option case sensitivity is honored when filtering option-name candidates: an entry declared case-insensitive is offered for a differently-cased prefix ('--FO' offers '--force') even when the global default is case-sensitive, matching what the parser accepts. Exercised at the shared source because a per-option override is otherwise not expressible through the attribute.")]
	public void When_SchemaEntryIsCaseInsensitive_Then_DifferentlyCasedPrefixStillMatches()
	{
		var entries = new[]
		{
			new Repl.Internal.Options.OptionSchemaEntry(
				"--force",
				"force",
				Repl.Internal.Options.OptionSchemaTokenKind.BoolFlag,
				ReplArity.ZeroOrOne,
				CaseSensitivity: ReplCaseSensitivity.CaseInsensitive),
		};
		var schema = new Repl.Internal.Options.OptionSchema(
			entries,
			new Dictionary<string, Repl.Internal.Options.OptionSchemaParameter>(StringComparer.OrdinalIgnoreCase));

		var results = new List<string>();
		Repl.Internal.Options.OptionTokenCompletionSource.CollectRouteOptionTokens(
			schema,
			"--FO",
			ReplCaseSensitivity.CaseSensitive,
			new HashSet<string>(StringComparer.Ordinal),
			results);

		results.Should().Contain("--force", because: "the entry is case-insensitive, so '--FO' matches it even under a case-sensitive global default");
	}

	[TestMethod]
	[Description("Shell enum-value completion recognizes short option aliases: after 'app run -m ' (where -m is a short alias for an enum option) the enum values are offered, matching what the parser accepts for '-m Debug'.")]
	public void When_ShellCompletesEnumValueAfterShortAlias_Then_EnumNamesAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption(Aliases = ["-m"])] ProbeMode mode) => mode.ToString())
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app run -m ";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().Contain("Debug", because: "'-m' is a short alias for the enum option; its values must complete like '--mode' does");
	}

	[TestMethod]
	[Description("Route options wait until OPTIONAL positionals are disambiguated too: 'run --' on 'run {profile?}' must not offer --force, because RouteResolver would bind --force to the optional {profile} before option parsing. Once the optional is filled ('run x --'), route options appear.")]
	public async Task When_OptionalPositionalIsUnfilled_Then_RouteOptionsAreNotSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {profile?}", static string (string? profile, [ReplOption] bool force) => profile ?? "none")
			.WithDescription("Run.");

		var unfilled = await ResolveAutocompleteAsync(sut, "run --").ConfigureAwait(false);
		var filled = await ResolveAutocompleteAsync(sut, "run zo --").ConfigureAwait(false);

		unfilled.Suggestions.Select(static s => s.Value).Should().NotContain("--force",
			because: "an unfilled optional positional would capture --force as its value");
		filled.Suggestions.Select(static s => s.Value).Should().Contain("--force",
			because: "once the optional positional is filled, route options are safe");
	}

	[TestMethod]
	[Description("A bare '--' bound to a positional does not terminate options: 'remote -- status --' binds '--' to {name}, the route is terminal, and the trailing current token asks for option names — the positional '--' must not suppress them.")]
	public async Task When_SeparatorIsBoundAsPositional_Then_OptionsAreStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("remote {name} status", static string (string name, [ReplOption] bool force) => name)
			.WithDescription("Remote status.");

		var result = await ResolveAutocompleteAsync(sut, "remote -- status --").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--force",
			because: "the '--' was consumed as {name}, so it is not the end-of-options separator here");
	}

	[TestMethod]
	[Description("Token classification uses the resolved route boundary: in 'remote -prod status' the '-prod' binds to {name}, so 'status' is classified against segment 2 as a Command literal, not mis-indexed to segment 1.")]
	public async Task When_DashPositionalPrecedes_Then_LaterLiteralClassifiesAsCommand()
	{
		var sut = CoreReplApp.Create();
		sut.Map("remote {name} status", static string (string name) => name)
			.WithDescription("Remote status.");
		const string input = "remote -prod status";

		var result = await ResolveAutocompleteAsync(sut, input).ConfigureAwait(false);

		var statusStart = input.IndexOf("status", StringComparison.Ordinal);
		var statusClass = result.TokenClassifications!.Single(c => c.Start == statusStart);
		statusClass.Kind.Should().Be(ConsoleLineReader.AutocompleteSuggestionKind.Command,
			because: "'-prod' fills {name}, so 'status' is the literal at segment 2");
	}

	[TestMethod]
	[Description("Interactive option completion offers output-format aliases case-insensitively, matching how GlobalOptionParser resolves them (OutputOptions.Aliases is a case-insensitive dictionary): '--J' offers '--json' even under a case-sensitive global default.")]
	public async Task When_OutputAliasPrefixIsDifferentlyCased_Then_AliasIsStillSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "show --J").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--json",
			because: "output aliases resolve case-insensitively, so '--J' matches '--json'");
	}

	[TestMethod]
	[Description("Shell completion expands unique command prefixes before resolving, like execution: 'app i pkg --' (where 'i' uniquely prefixes 'install') resolves the install route and offers its options.")]
	public void When_ShellCompletesAfterUniquePrefix_Then_RouteOptionsAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {name}", static string (string name, [ReplOption] bool force) => name)
			.WithDescription("Install a skill.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app i pkg --";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().Contain("--force", because: "'i' uniquely prefixes 'install', so its route options must be offered");
	}

	[TestMethod]
	[Description("Shell enum completion must not fire for a dash token that routing consumed as a positional: 'app deploy -m ' binds '-m' to {target}, so no enum values are offered (accepting one would leave it as stray positional text).")]
	public void When_DashTokenWasBoundAsPositional_Then_ShellOffersNoEnumValues()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target, [ReplOption(Aliases = ["-m"])] ProbeMode mode) => target)
			.WithDescription("Deploy.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app deploy -m ";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().NotContain("Debug", because: "'-m' was bound to {target} by routing, so it is not a pending option here");
	}

	[TestMethod]
	[Description("A valid leading global option keeps its option classification: '--no-logo show' classifies '--no-logo' as a Parameter (option), not Invalid — global options are executable before the command, independent of the route-option region.")]
	public async Task When_GlobalOptionPrecedesCommand_Then_ItClassifiesAsOption()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show", static string () => "ok").WithDescription("Show.");
		const string input = "--no-logo show";

		var result = await ResolveAutocompleteAsync(sut, input).ConfigureAwait(false);

		var noLogo = result.TokenClassifications!.Single(c => c.Start == 0);
		noLogo.Kind.Should().Be(ConsoleLineReader.AutocompleteSuggestionKind.Parameter,
			because: "a global option before the command is valid, not Invalid");
		var showStart = input.IndexOf("show", StringComparison.Ordinal);
		result.TokenClassifications!.Single(c => c.Start == showStart).Kind
			.Should().Be(ConsoleLineReader.AutocompleteSuggestionKind.Command);
	}

	[TestMethod]
	[Description("Classification cost stays flat per completion rather than scaling with the token count: a long trailing token run must not blow up (it previously re-resolved the whole prefix per token). This pins that a wide input still classifies quickly and correctly.")]
	public async Task When_ManyTrailingTokens_Then_ClassificationStaysBounded()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {name}", static string (string name, [ReplOption] bool force) => name).WithDescription("Run.");
		var input = "run zo " + string.Join(' ', Enumerable.Range(0, 200).Select(static i => $"a{i}"));

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var result = await ResolveAutocompleteAsync(sut, input).ConfigureAwait(false);
		stopwatch.Stop();

		result.TokenClassifications!.Should().HaveCount(202);
		stopwatch.ElapsedMilliseconds.Should().BeLessThan(500,
			because: "classification resolves once, not once per token");
	}

	[TestMethod]
	[Description("The '--output:<format>' transformer selector is offered for a differently-cased prefix ('--output:J' offers '--output:json'), because transformer names resolve through a case-insensitive dictionary like output aliases.")]
	public async Task When_OutputSelectorPrefixIsDifferentlyCased_Then_TransformerIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "show --output:J").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--output:json",
			because: "transformer names resolve case-insensitively");
	}

	[TestMethod]
	[Description("Shell completion preserves case-distinct executable option aliases under case-sensitive option parsing: a route with '-m' and '-M' bound to different parameters offers BOTH, rather than collapsing them with a case-insensitive dedupe.")]
	public void When_OptionsAreCaseSensitive_Then_ShellKeepsBothCaseDistinctAliases()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map(
				"run",
				static string ([ReplOption(Aliases = ["-m"])] string? mode, [ReplOption(Aliases = ["-M"])] string? mask) => mode ?? mask ?? "none")
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app run -";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().Contain("-m").And.Contain("-M",
			because: "case-sensitive parsing binds -m and -M to different parameters; both are executable");
	}

	[TestMethod]
	[Description("A later '--' separator must not retroactively invalidate an earlier option: in 'run --force --' the '--force' before the separator stays classified as a Parameter (option), because it executes with force=true.")]
	public async Task When_SeparatorFollowsAnOption_Then_EarlierOptionStaysClassifiedAsOption()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] bool force) => force.ToString()).WithDescription("Run.");
		const string input = "run --force --";

		var result = await ResolveAutocompleteAsync(sut, input).ConfigureAwait(false);

		var forceStart = input.IndexOf("--force", StringComparison.Ordinal);
		result.TokenClassifications!.Single(c => c.Start == forceStart).Kind
			.Should().Be(ConsoleLineReader.AutocompleteSuggestionKind.Parameter,
				because: "an option before the '--' separator remains a valid option");
	}

	[TestMethod]
	[Description("Global-option marking uses the parser's exact consumed indices, not string matching: with a valued global '--tenant' and a 'show' command, 'show' is the command and the tenant VALUE 'show' is the consumed global value — the second 'show' classifies as Command, not the first.")]
	public async Task When_GlobalValueEqualsCommandName_Then_TheCommandTokenClassifiesAsCommand()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", static string () => "ok").WithDescription("Show.");
		const string input = "--tenant show show";

		var result = await ResolveAutocompleteAsync(sut, input).ConfigureAwait(false);

		// The command token is the SECOND "show"; the first is the tenant value.
		var secondShowStart = input.LastIndexOf("show", StringComparison.Ordinal);
		result.TokenClassifications!.Single(c => c.Start == secondShowStart).Kind
			.Should().Be(ConsoleLineReader.AutocompleteSuggestionKind.Command,
				because: "the parser consumes the first 'show' as the tenant value and the second as the command");
	}

	[TestMethod]
	[Description("A pending valued global option suppresses command suggestions: after '--tenant ' the current token is the tenant VALUE, so completing a root command ('install') would misparse as the value — no command names are offered.")]
	public async Task When_ValuedGlobalOptionAwaitsValue_Then_NoCommandIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("install {skillName}", static string (string skillName) => skillName).WithDescription("Install.");

		// Partial command token so the assertion is discriminating: a valued global suppresses
		// it (it is the tenant value), whereas a bool global would still complete it.
		var result = await ResolveAutocompleteAsync(sut, "--tenant ins").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("install",
			because: "the token after a pending valued global is its value, not a command");
	}

	[TestMethod]
	[Description("A pending valued route option suppresses option-name suggestions: after 'run --channel -' the current token is --channel's VALUE, so offering '--force' would misparse (--channel left without a value) — no option names are offered.")]
	public async Task When_ValuedRouteOptionAwaitsValue_Then_NoOptionNameIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel, [ReplOption] bool force) => channel ?? "none")
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel -").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("--force",
			because: "the token after a pending valued route option is its value, not another option");
	}

	[TestMethod]
	[Description("Built-in result-flow options are offered in completion: typing '--res' surfaces '--result:page-size' and friends, matching what GlobalOptionParser accepts and help documents.")]
	public async Task When_ResultFlowPrefixIsTyped_Then_ResultFlowOptionsAreSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "show --res").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("--result:page-size").And.Contain("--result:cursor");
	}

	[TestMethod]
	[Description("A pending valued built-in result-flow option suppresses command suggestions: after '--result:page-size ' the current token is its value, so a root command must not be offered (it would be consumed as the page size).")]
	public async Task When_ResultFlowOptionAwaitsValue_Then_NoCommandIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("install {skillName}", static string (string skillName) => skillName).WithDescription("Install.");

		var result = await ResolveAutocompleteAsync(sut, "--result:page-size install").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("install",
			because: "the token after a pending valued result-flow option is its value");
	}

	[TestMethod]
	[Description("A pending valued global does NOT suppress completion when the current token is option-like: '--tenant --' offers options, because the global parser does not consume a dash-prefixed token as the value ('--tenant' takes its fallback and '--' is a separate token).")]
	public async Task When_ValuedGlobalIsFollowedByOptionPrefix_Then_OptionsAreStillOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "--tenant --").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--help",
			because: "'--tenant' cannot consume the option-like '--' as its value, so options remain available");
	}

	[TestMethod]
	[Description("A '--' on a route that also declares a valued option is not a pending value position, and it does not reopen positional completion either: for 'deploy x -- -' the {target} value is bound, so neither channel's pending path nor target's provider may offer values (issue #45 parity).")]
	public async Task When_SeparatorFollowsPendingLikeToken_Then_ProviderDoesNotFireOnOptionRegion()
	{
		var sut = CoreReplApp.Create();
		sut.Map("deploy {target}", static string (string target, [ReplOption] string? channel) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithDescription("Deploy.");

		var result = await ResolveAutocompleteAsync(sut, "deploy x -- -").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("zo-profile",
			because: "{target} is bound and '--' opened the option region; no positional value can bind past it");
	}

	[TestMethod]
	[Description("Shell parity: a pending valued route option suppresses option-name candidates in shell completion too — 'app run --channel -' must not offer '-f' (which would leave --channel without a value).")]
	public void When_ShellRouteOptionAwaitsValue_Then_NoOptionNameIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption(Aliases = ["-f"])] bool force, [ReplOption] string? channel) => channel ?? "none")
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app run --channel -";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().NotContain("-f", because: "--channel awaits a value; offering another option would misparse");
	}

	[TestMethod]
	[Description("Shell parity: a pending valued global suppresses candidates in shell completion too — after 'app run --mode --tenant ' no enum value for --mode is offered (it would be consumed as the tenant value).")]
	public void When_ShellGlobalAwaitsValue_Then_NoStaleEnumIsOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("run", static string ([ReplOption(Aliases = ["-m"])] ProbeMode mode) => mode.ToString())
			.WithDescription("Run.");
		var shellEngine = new ShellCompletionEngine(sut);
		const string line = "app run --mode --tenant ";

		var candidates = shellEngine.ResolveShellCompletionCandidates(line, line.Length);

		candidates.Should().NotContain("Debug", because: "--tenant awaits its value; --mode's enum values must not leak here");
	}

	[TestMethod]
	[Description("A pending valued option still offers its VALUE completions: with a WithCompletion provider on the option's target, 'run --channel ' returns the provider's values — only command and option-NAME candidates are suppressed, not the option's own value provider.")]
	public async Task When_ValuedRouteOptionAwaitsValue_Then_ItsProviderStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha", "beta"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("alpha").And.Contain("beta",
			because: "the pending option's value provider must still run — only command/option names are suppressed");
	}

	[TestMethod]
	[Description("A BOOL global is not a pending value, so a command still completes after it: '--verbose sh' offers 'show' (the bool consumes nothing). Resolving through the parser's token map — not an independent definition scan — keeps completion's verdict identical to the parser's even when aliases collide.")]
	public async Task When_TokenResolvesToBoolGlobal_Then_CommandStillCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<bool>("verbose"));
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "--verbose sh").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("show",
			because: "a bool global consumes no value, so the following partial token still completes as a command");
	}

	[TestMethod]
	[Description("No subcommand is offered once a route option has been typed: for routes 'parent' ([ReplOption] bool force) and 'parent child', 'parent --force c' must not suggest 'child' — the option occupies the segment position and a bool flag would swallow the word, so the child route is unreachable there.")]
	public async Task When_OptionPrecedesSubcommandPosition_Then_SubcommandIsNotSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("parent", static string ([ReplOption] bool force) => force.ToString()).WithDescription("Parent.");
		sut.Map("parent child", static string () => "child").WithDescription("Child.");

		var result = await ResolveAutocompleteAsync(sut, "parent --force c").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("child",
			because: "an option already occupies the position; 'parent --force child' would not invoke the child route");
	}

	[TestMethod]
	[Description("Result-flow pending honors option case sensitivity: under CaseInsensitive parsing, GlobalOptionParser accepts '--RESULT:PAGE-SIZE' and consumes the next token as its page-size value, so completion must treat the following partial token as that value and NOT offer a command ('install').")]
	public async Task When_ResultFlowOptionAwaitsValueDifferentlyCased_Then_NoCommandIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map("install {skillName}", static string (string skillName) => skillName).WithDescription("Install.");

		var result = await ResolveAutocompleteAsync(sut, "--RESULT:PAGE-SIZE ins").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("install",
			because: "under case-insensitive parsing the token after '--RESULT:PAGE-SIZE' is its value, not a command");
	}

	[TestMethod]
	[Description("The built-in '--answer:' prefill is offered in completion: GlobalOptionParser accepts '--answer:<name>[=value]' as a global flag (documented), so typing '--ans' must surface '--answer:' like the other static globals.")]
	public async Task When_AnswerPrefixIsTyped_Then_AnswerOptionIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "show --ans").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--answer:",
			because: "'--answer:' is a documented built-in global flag and must be completable");
	}

	[TestMethod]
	[Description("No context is offered once a route option has been typed: with a 'parent' route ([ReplOption] bool force) and a 'parent child' context, 'parent --force c' must not suggest the 'child' context — the option region is active, so execution treats 'c' as the route's trailing option text, not a context entry.")]
	public async Task When_OptionPrecedesContextPosition_Then_ContextIsNotSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("parent", static string ([ReplOption] bool force) => force.ToString()).WithDescription("Parent.");
		sut.Context("parent", parent => parent.Context("child", child => child.Map("go", static string () => "ok").WithDescription("Go.")));

		var result = await ResolveAutocompleteAsync(sut, "parent --force c").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("child",
			because: "an option already occupies the position; execution treats 'c' as the route's trailing option text, not a context entry");
	}

	[TestMethod]
	[Description("A pending option value invokes ONLY that option's provider, not the route's other providers: for 'run {target}' with a provider on 'target' AND a [ReplOption] string channel with its own provider, 'run app --channel ' offers channel's values (beta) and NOT target's (zo-profile) — reusing the route's sole/first provider would bind the wrong parameter's values.")]
	public async Task When_PendingOptionHasOwnProvider_Then_OnlyThatProviderCompletes()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {target}", static string (string target, [ReplOption] string? channel) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha", "beta"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run app --channel ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("beta", because: "the pending option's own value provider must run");
		values.Should().NotContain("zo-profile",
			because: "the target positional's provider must not leak into the channel value menu");
	}

	[TestMethod]
	[Description("A pending option with no provider offers nothing — it does not fall back to the route's single registered provider: for 'run {target}' with a provider on 'target' only and a [ReplOption] string channel, 'run app --channel ' must NOT offer target's values as channel values.")]
	public async Task When_PendingOptionHasNoProvider_Then_OtherProviderDoesNotLeak()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run {target}", static string (string target, [ReplOption] string? channel) => target)
			.WithCompletion("target", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["zo-profile"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run app --channel ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("zo-profile",
			because: "channel has no provider; the target positional's provider must not be invoked for it");
	}

	[TestMethod]
	[Description("A pending GLOBAL value after a route option must not invoke the route option's provider: ResolveCommitted strips the global before route resolution, so terminalRoute.RemainingTokens can still end with an earlier route option ('--channel'). For 'run app --channel --tenant ' the pending value is the global tenant's, so channel's provider must NOT run.")]
	public async Task When_PendingGlobalFollowsRouteOption_Then_RouteProviderIsNotInvoked()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("run {target}", static string (string target, [ReplOption] string? channel) => target)
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["alpha", "beta"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run app --channel --tenant ").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("alpha").And.NotContain("beta",
			because: "the pending value is the global tenant's, not channel's — channel's provider must not run");
	}

	[TestMethod]
	[Description("The '--answer:' prefill matches case-insensitively regardless of OptionCaseSensitivity: GlobalOptionParser.TryParsePromptAnswer accepts '--ANSWER:name' via OrdinalIgnoreCase even under CaseSensitive options, so 'show --ANS' must still surface '--answer:'.")]
	public async Task When_AnswerPrefixIsUpperCasedUnderCaseSensitive_Then_AnswerOptionIsSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseSensitive);
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "show --ANS").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("--answer:",
			because: "TryParsePromptAnswer matches '--answer:' case-insensitively, so completion must too");
	}

	[TestMethod]
	[Description("A pending option value completion drops values the invocation parser would not consume as a separate value: a dash-prefixed candidate ('--prod') is treated as the next option, so accepting it would leave the option unset. 'run --channel ' with a provider returning '--prod', 'alpha', '-42' offers 'alpha' and the signed numeric '-42' (the parser binds it as a value) but not '--prod'.")]
	public async Task When_PendingOptionProviderReturnsDashValue_Then_ItIsNotOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["--prod", "alpha", "-42"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("alpha").And.Contain("-42",
			because: "plain values and signed numeric literals are consumable as the option's separate value");
		values.Should().NotContain("--prod",
			because: "a dash-prefixed candidate is parsed as the next option, so it cannot fill --channel");
	}

	[TestMethod]
	[Description("A pending enum option completes its member names on the interactive path too (parity with shell): for '[ReplOption] ProbeMode mode', 'run --mode D' offers 'Debug'. The pending path resolves the option's parameter and, when it is an enum, adds member names with the parameter's effective case sensitivity.")]
	public async Task When_PendingEnumOptionAwaitsValue_Then_EnumMembersAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] ProbeMode mode) => mode.ToString()).WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --mode D").ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().Contain("Debug",
			because: "a pending enum option must complete its member names like shell completion does");
	}

	[TestMethod]
	[Description("Ambient commands follow the same option-region guard as commands and contexts: in a 'parent' scope whose route carries '[ReplOption] bool force', '--force h' must not offer the ambient 'help' — accepting it produces routed option text, not an ambient invocation.")]
	public async Task When_OptionPrecedesAmbientPosition_Then_AmbientIsNotSuggested()
	{
		var sut = CoreReplApp.Create();
		sut.Map("parent", static string ([ReplOption] bool force) => force.ToString()).WithDescription("Parent.");

		var result = await ResolveAutocompleteAsync(sut, "--force h", scopeTokens: ["parent"]).ConfigureAwait(false);

		result.Suggestions.Select(static s => s.Value).Should().NotContain("help",
			because: "an option already occupies the position; 'parent --force help' is routed option text, not an ambient command");
	}

	[TestMethod]
	[Description("A pending option value with no provider is not flagged invalid in the live hint: 'run --channel alpha' (a valued string option, no completion provider) accepts 'alpha' at execution, so the hint must not read 'Invalid: alpha'.")]
	public async Task When_PendingOptionValueHasNoProvider_Then_HintIsNotInvalid()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none").WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel alpha").ConfigureAwait(false);

		(result.HintLine ?? string.Empty).Should().NotContain("Invalid",
			because: "the pending value is a free-form option value the parser accepts, not an invalid token");
	}

	[TestMethod]
	[Description("A pending option value that the parser will NOT consume is still flagged invalid in the hint: 'run --channel --prod' leaves --channel unfilled (a dash-prefixed token is read as the next option, not the value), so the hint reads 'Invalid: --prod' rather than being suppressed like a consumable free-form value.")]
	public async Task When_PendingOptionValueIsOptionLike_Then_HintIsInvalid()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none").WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel --prod").ConfigureAwait(false);

		(result.HintLine ?? string.Empty).Should().Contain("Invalid",
			because: "a dash-prefixed token is not consumed as the option value, so it is invalid there");
	}

	[TestMethod]
	[Description("Pending option value completion preserves case-distinct provider values: a string option's value is case-significant at execution, so a provider returning 'Prod' and 'prod' offers both — they must not collapse under the UI's case-insensitive dedupe.")]
	public async Task When_PendingProviderReturnsCaseDistinctValues_Then_BothAreOffered()
	{
		var sut = CoreReplApp.Create();
		sut.Map("run", static string ([ReplOption] string? channel) => channel ?? "none")
			.WithCompletion("channel", static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["Prod", "prod"]))
			.WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --channel ").ConfigureAwait(false);

		var values = result.Suggestions.Select(static s => s.Value).ToArray();
		values.Should().Contain("Prod").And.Contain("prod",
			because: "a string option value is case-significant, so both distinct values must survive dedupe");
	}

	[TestMethod]
	[Description("A pending result-flow option keeps its Invalid hint for a signed-numeric token: GlobalOptionParser consumes a result-flow value only when it does NOT start with '-' (even '-1' is rejected), unlike the general option parser which binds '-42'. So '--result:page-size -1' must still read 'Invalid: -1' rather than being suppressed.")]
	public async Task When_PendingResultFlowOptionValueIsSignedNumeric_Then_HintIsInvalid()
	{
		var sut = CoreReplApp.Create();
		sut.Map("show", static string () => "ok").WithDescription("Show.");

		var result = await ResolveAutocompleteAsync(sut, "--result:page-size -1").ConfigureAwait(false);

		(result.HintLine ?? string.Empty).Should().Contain("Invalid",
			because: "result-flow options do not consume a dash-prefixed token (even -1) as their value, so it is invalid there");
	}

	[TestMethod]
	[Description("Pending enum value completion dedupes by the enum's effective case sensitivity, not the UI comparer: for a case-distinct enum under case-insensitive parsing, execution maps both spellings to the first member, so 'run --mode p' offers a single candidate (matching shell) rather than both 'Prod' and 'prod'.")]
	public async Task When_PendingEnumHasCaseDistinctMembers_Then_EffectiveSensitivityDedupes()
	{
		var sut = CoreReplApp.Create();
		sut.Options(options => options.Parsing.OptionCaseSensitivity = ReplCaseSensitivity.CaseInsensitive);
		sut.Map("run", static string ([ReplOption] CaseVariantMode mode) => mode.ToString()).WithDescription("Run.");

		var result = await ResolveAutocompleteAsync(sut, "run --mode p").ConfigureAwait(false);

		result.Suggestions
			.Count(static s => string.Equals(s.Value, "Prod", StringComparison.OrdinalIgnoreCase))
			.Should().Be(1, because: "under case-insensitive parsing both spellings map to the same member, so only one is offered");
	}

private enum CaseVariantMode
	{
		Prod,
		prod,
	}

private enum ProbeMode
	{
		Debug,
		Release,
	}

	private static async Task<ConsoleLineReader.AutocompleteResult> ResolveAutocompleteAsync(
		CoreReplApp app,
		string input,
		IReadOnlyList<string>? scopeTokens = null)
	{
		var result = await app.Autocomplete.ResolveAutocompleteAsync(
			new ConsoleLineReader.AutocompleteRequest(input, input.Length, MenuRequested: true),
			scopeTokens ?? [],
			EmptyServiceProvider.Instance,
			CancellationToken.None)
			.ConfigureAwait(false);
		result.Should().NotBeNull();
		return result!.Value;
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
