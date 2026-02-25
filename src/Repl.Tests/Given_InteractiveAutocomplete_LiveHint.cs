using ComponentDescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractiveAutocomplete_LiveHint
{
	[TestMethod]
	[Description("Live hint shows top command matches while typing without pressing Tab.")]
	public void When_TypingCommandPrefix_Then_LiveHintShowsMatches()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send", () => "ok").WithDescription("Publish a message");
		sut.Map("sessions", () => "ok").WithDescription("List active sessions");
		sut.Map("secret", () => "ok").WithDescription("Hidden command").Hidden();

		var harness = new TerminalHarness(cols: 64, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Matches: send, sessions");
		harness.RawOutput.Should().NotContain("secret");
	}

	[TestMethod]
	[Description("Live hint switches to current route parameter details once command route is fixed.")]
	public void When_OnRouteParameter_Then_LiveHintShowsParameterNameAndDescription()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send {message}", (Func<string, string>)SendWithMessage).WithDescription("Send text payload");

		var harness = new TerminalHarness(cols: 72, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Param message: Message sent to all watching sessions");
	}

	[TestMethod]
	[Description("Live hint reports overload alternatives when multiple route signatures are valid at current position.")]
	public void When_MultipleOverloadsMatch_Then_LiveHintShowsAlternatives()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("who {id:int}", (Func<int, string>)(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		sut.Map("who {name:alpha}", (Func<string, string>)(name => name));

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.W, 'w'),
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Overloads:");
		harness.RawOutput.Should().Contain("who {id:int}");
		harness.RawOutput.Should().Contain("who {name:alpha}");
	}

	[TestMethod]
	[Description("When both literals and dynamic overloads exist at current segment, live hint prioritizes literal command matches.")]
	public void When_LiteralAndDynamicAlternativesExist_Then_HintShowsLiteralMatchesFirst()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("client add", () => "ok").WithDescription("Add a client");
		sut.Map("client list", () => "ok").WithDescription("List clients");
		sut.Map("client {id} show", (Func<int, string>)(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Matches: add, list");
	}

	[TestMethod]
	[Description("When current token does not match any literal alternative at current segment, live hint does not fall back to unrelated overload labels.")]
	public void When_CurrentTokenHasNoLiteralMatch_Then_HintDoesNotShowUnrelatedOverloads()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("client add", () => "ok").WithDescription("Add a client");
		sut.Map("client list", () => "ok").WithDescription("List clients");
		sut.Map("client {id} remove", (Func<int, string>)(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		sut.Map("client {id} show", (Func<int, string>)(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		sut.Map("client {id} update {label}", (Func<int, string, string>)((id, label) => label));

		var harness = new TerminalHarness(cols: 100, rows: 14);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.A, 'a'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.V, 'v'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().NotContain("Matches: remove, show, update");
	}

	[TestMethod]
	[Description("When a dynamic context branch is also valid at the current segment, live hint includes its placeholder.")]
	public void When_LiteralAndDynamicContextMatch_Then_HintIncludesContextPlaceholder()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Context("client", client =>
		{
			client.Map("list", () => "ok").WithDescription("List clients");
			client.Context("{id}", branch => branch.Map("show", (Func<string, string>)(id => id)));
		});

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Matches:");
		harness.RawOutput.Should().Contain("{id}");
	}

	[TestMethod]
	[Description("Global help token is recognized by live hint and is not reported as invalid.")]
	public void When_TypingHelpToken_Then_LiveHintDoesNotShowInvalid()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send", () => "ok").WithDescription("Publish a message");

		var harness = new TerminalHarness(cols: 72, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Command: help");
		harness.RawOutput.Should().NotContain("Invalid: help");
	}

	[TestMethod]
	[Description("Help path argument is classified as a valid argument and not rendered with invalid/error token style.")]
	public void When_TypingHelpWithPath_Then_PathTokenIsNotInvalid()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Context("contact", scope => scope.Map("list", () => "ok").WithDescription("List contacts"));

		var harness = new TerminalHarness(cols: 80, rows: 14);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.A, 'a'),
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().Contain("Commands:");
		harness.RawOutput.Should().NotContain("Invalid: contactt");
		harness.RawOutput.Should().NotContain("\u001b[38;5;203mcontactt\u001b[0m");
	}

	[TestMethod]
	[Description("Quoted multi-word argument is treated as a single token and does not produce Invalid hints.")]
	public void When_TypingQuotedArgument_Then_LiveHintDoesNotShowInvalid()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send {message}", (Func<string, string>)SendWithMessage).WithDescription("Send text payload");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Oem7, '"'),
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.W, 'w'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.R, 'r'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Oem7, '"'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().NotContain("Invalid:");
	}

	[TestMethod]
	[Description("Unclosed quote at end-of-input keeps the partial argument as a single token with parameter hint.")]
	public void When_TypingUnclosedQuote_Then_LiveHintShowsParameterHint()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send {message}", (Func<string, string>)SendWithMessage).WithDescription("Send text payload");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Oem7, '"'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.A, 'a'),
			Key(ConsoleKey.R, 'r'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.A, 'a'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		RunInteractive(sut, harness, keyReader);

		harness.RawOutput.Should().NotContain("Invalid:");
		harness.RawOutput.Should().Contain("Param message");
	}

	private static string SendWithMessage([ComponentDescriptionAttribute("Message sent to all watching sessions")] string message) => message;

	private static void RunInteractive(ReplApp sut, TerminalHarness harness, FakeKeyReader keyReader)
	{
		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;
			_ = sut.Run([]);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
