using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractiveAutocomplete_Menu
{
	[TestMethod]
	[Description("Regression guard: rich autocomplete menu excludes hidden commands, renders descriptions, and crops to terminal width.")]
	public void When_MenuIsRendered_Then_HiddenCommandsAreExcluded_AndDescriptionsAreCropped()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send", () => "ok").WithDescription("Publish a message to all watching sessions");
		sut.Map("sessions", () => "ok").WithDescription("List active sessions with transport and activity details");
		sut.Map("secret", () => "nope").WithDescription("Hidden command").Hidden();

		var harness = new TerminalHarness(cols: 40, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.DownArrow),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (40, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			harness.RawOutput.Should().Contain("send");
			harness.RawOutput.Should().Contain("sessions");
			harness.RawOutput.Should().Contain("Publish");
			harness.RawOutput.Should().Contain("List");
			harness.RawOutput.Should().Contain("...");
			harness.RawOutput.Should().NotContain("secret");
			harness.RawOutput.Should().Contain("\u001b[J");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Regression guard: when the current token already matches a command exactly, Tab does not re-suggest peer commands.")]
	public void When_CurrentTokenMatchesExactly_Then_TabDoesNotReshowSameLevelCommands()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("send", () => "ok").WithDescription("Publish a message");
		sut.Map("sessions", () => "ok").WithDescription("List sessions");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.S, 's'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.N, 'n'),
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			harness.RawOutput.Should().NotMatchRegex(@"(?m)^\s*sessions\s*-\s*List sessions\s*$");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Issue #45 end-to-end: pressing Tab while TYPING a parameter value drives the real ConsoleLineReader menu with the WithCompletion provider's candidates — 'deploy zo' + Tab renders zo-ga/zo-bu, not just the parameter hint.")]
	public void When_TabIsPressedWhileTypingParameterValue_Then_MenuShowsProviderCandidates()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("deploy {clientId}", static string (string clientId) => clientId)
			.WithCompletion("clientId", static (_, _, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(["zo-ga", "zo-bu"]))
			.WithDescription("Deploy a client.");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Y, 'y'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Z, 'z'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			harness.RawOutput.Should().Contain("zo-ga", because: "the provider's candidates must reach the rendered Tab menu while the value is typed");
			harness.RawOutput.Should().Contain("zo-bu");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Accept-then-execute round-trip for a provider value containing spaces: typing 'greet Ne', accepting the provider's 'New York' suggestion from the Tab menu, and executing must hand the HANDLER the single value 'New York' — the inserted text is pre-quoted so tokenization does not split it.")]
	public void When_ProviderValueWithSpacesIsAcceptedFromMenu_Then_HandlerReceivesSingleValue()
	{
		string? capturedName = null;
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("greet {name}", (string name) =>
		{
			capturedName = name;
			return name;
		})
			.WithCompletion("name", static (_, _, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(["New York"]))
			.WithDescription("Greet.");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.G, 'g'),
			Key(ConsoleKey.R, 'r'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.N, 'N'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			capturedName.Should().Be("New York",
				because: "the accepted suggestion must round-trip through tokenization as one argument");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Provider values carrying terminal control sequences (OSC/CSI/BEL, C1) are rejected on the INTERACTIVE surface too: a value embedding an OSC title change must never reach the rendered output — the same control-character rule as the shell bridge.")]
	public void When_ProviderValueCarriesTerminalControls_Then_MenuNeverRendersThem()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(["\u001b]0;forged-title\u0007safe", "clean"]))
			.WithDescription("Deploy.");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Y, 'y'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.C, 'c'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.Escape),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			harness.RawOutput.Should().NotContain("forged-title",
				because: "an OSC sequence in a provider value must never reach the user's terminal");
			harness.RawOutput.Should().Contain("clean", because: "well-formed values are still offered");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("The FIRST Tab at an empty value position surfaces provider candidates: pressing Tab once on 'deploy ' (Hybrid presentation, where the first Tab does not yet open the menu) must still invoke the WithCompletion provider and render its candidates — a Tab is always an explicit completion request even when it does not open the menu.")]
	public void When_FirstTabAtEmptyValue_Then_ProviderCandidatesAreOffered()
	{
		// A single candidate makes the first Tab inline-complete the whole value, so its
		// appearance in the output proves the provider was invoked on that first Tab.
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("deploy {target}", static string (string target) => target)
			.WithCompletion("target", static (_, _, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(["zulu-target"]))
			.WithDescription("Deploy.");

		var harness = new TerminalHarness(cols: 80, rows: 12);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.D, 'd'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.P, 'p'),
			Key(ConsoleKey.L, 'l'),
			Key(ConsoleKey.O, 'o'),
			Key(ConsoleKey.Y, 'y'),
			Key(ConsoleKey.Spacebar, ' '),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.X, 'x'),
			Key(ConsoleKey.I, 'i'),
			Key(ConsoleKey.T, 't'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (80, 12);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var exitCode = sut.Run([]);

			exitCode.Should().Be(0);
			harness.RawOutput.Should().Contain("zulu-target",
				because: "the first Tab is an explicit completion request even when it does not open the menu");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
