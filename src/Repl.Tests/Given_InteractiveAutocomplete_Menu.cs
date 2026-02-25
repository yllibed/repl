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

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
