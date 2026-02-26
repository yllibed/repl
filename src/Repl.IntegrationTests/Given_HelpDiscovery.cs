using ComponentDescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HelpDiscovery
{
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

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--output:toml"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: unknown output format 'toml'.");
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
		output.Text.Should().Contain("Parameters:");
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
		output.Text.Should().NotContain("Parameters:");
	}

	private static string SendHandler([ComponentDescriptionAttribute("Message to send to all watching sessions")] string message) => message;
}
