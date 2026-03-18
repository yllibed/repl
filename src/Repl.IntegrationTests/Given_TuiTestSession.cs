using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TuiTestSession
{
	[TestMethod]
	[Description("Verifies that a simple command produces expected text on the virtual terminal screen.")]
	public async Task When_RunningCommand_Then_OutputAppearsOnScreen()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		tui.GetScreenText().Should().Contain("world");
	}

	[TestMethod]
	[Description("Verifies that multiple commands execute in sequence and output accumulates.")]
	public async Task When_RunningMultipleCommands_Then_AllOutputVisible()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("ping", () => "pong");
		app.Map("fizz", () => "buzz");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("ping --no-logo");
		await tui.WaitForTextAsync("pong");

		tui.SendLine("fizz --no-logo");
		await tui.WaitForTextAsync("buzz");

		var screen = tui.GetScreenText();
		screen.Should().Contain("pong");
		screen.Should().Contain("buzz");
	}

	[TestMethod]
	[Description("Verifies that the prompt reappears after a command completes, indicating the session is ready.")]
	public async Task When_CommandCompletes_Then_PromptReappears()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("noop", () => "done");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("noop --no-logo");
		await tui.WaitForTextAsync("done");
		await tui.WaitForIdleAsync();

		// After a command completes, the REPL should show a prompt containing ">"
		tui.GetScreenText().Should().Contain(">");
	}

	[TestMethod]
	[Description("Verifies that WaitForTextAsync throws TimeoutException when expected text never appears.")]
	public async Task When_WaitingForMissingText_Then_ThrowsTimeout()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		var act = () => tui.WaitForTextAsync("never-gonna-appear", timeout: TimeSpan.FromMilliseconds(200));
		await act.Should().ThrowAsync<TimeoutException>();
	}

	[TestMethod]
	[Description("Verifies that GetCell returns non-default color for ANSI-colored output.")]
	public async Task When_OutputContainsAnsiColor_Then_CellHasColorAttributes()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		// ESC[32m = green foreground, ESC[0m = reset
		app.Map("green", () => "\x1b[32mGREEN\x1b[0m");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("green --no-logo");
		await tui.WaitForTextAsync("GREEN");

		var row = tui.FindLineContaining("GREEN");
		row.Should().BeGreaterThanOrEqualTo(0, "the GREEN text should be visible on screen");

		var cell = tui.FindFirstNonBlankCell(row);
		cell.Should().NotBeNull("the row containing GREEN should have non-blank cells");

		// The cell should have a non-default foreground color applied
		// (exact mode/value depends on XTerm.NET's interpretation of SGR 32)
		(cell!.FgColor != 0 || cell.FgMode != 0).Should().BeTrue(
			"the cell should have a non-default foreground color from the ANSI escape");
	}

	[TestMethod]
	[Description("Verifies that GetLine returns the correct text for a specific row.")]
	public async Task When_ReadingSpecificLine_Then_ReturnsCorrectText()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("say", () => "hello from line");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("say --no-logo");
		await tui.WaitForTextAsync("hello from line");

		var row = tui.FindLineContaining("hello from line");
		row.Should().BeGreaterThanOrEqualTo(0);

		var line = tui.GetLine(row);
		line.Should().Contain("hello from line");
	}

	[TestMethod]
	[Description("Verifies that StopAsync returns an exit code after completing the session.")]
	public async Task When_StoppingSession_Then_ReturnsExitCode()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		var exitCode = await tui.StopAsync();
		exitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Verifies that RawOutput captures the unprocessed ANSI output from the REPL.")]
	public async Task When_CommandProducesOutput_Then_RawOutputContainsAnsi()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		tui.RawOutput.Should().Contain("world");
	}

	[TestMethod]
	[Description("Verifies that Frames captures screen snapshots as output is written.")]
	public async Task When_CommandProducesOutput_Then_FramesCaptured()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		tui.Frames.Should().NotBeEmpty("at least one frame should have been captured");
	}

	[TestMethod]
	[Description("Verifies that WaitForIdleAsync returns once output stabilizes.")]
	public async Task When_OutputStabilizes_Then_WaitForIdleReturns()
	{
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: 24);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		// Should not throw — output has already stabilized
		await tui.WaitForIdleAsync(quietPeriod: TimeSpan.FromMilliseconds(300));
	}

	[TestMethod]
	[Description("Verifies that GetVisibleLines returns the correct number of lines matching terminal height.")]
	public async Task When_ReadingVisibleLines_Then_CountMatchesTerminalRows()
	{
		const int rows = 16;
		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("hello", () => "world");

		await using var tui = new TuiTestSession(app, cols: 80, rows: rows);
		await tui.StartAsync();

		tui.SendLine("hello --no-logo");
		await tui.WaitForTextAsync("world");

		var lines = tui.GetVisibleLines();
		lines.Should().HaveCount(rows);
	}
}
