using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ConsoleLineReader_RenderingEngine
{
	[TestMethod]
	[Description("Rich assist near viewport bottom keeps rendered prompt/cursor stable in terminal-engine frames.")]
	public async Task When_RichAssistRendersAtViewportBottom_Then_FramesContainConsistentPromptAndHint()
	{
		var harness = new TerminalHarness(cols: 48, rows: 6);
		var keyReader = new FakeKeyReader([Key(ConsoleKey.H, 'h'), Key(ConsoleKey.Tab, '\t'), Key(ConsoleKey.Tab, '\t'), Key(ConsoleKey.Enter, '\r'), Key(ConsoleKey.Enter, '\r')]);
		var result = await RunReadAsync(harness, keyReader, StaticResolverAsync, primeBottom: true).ConfigureAwait(false);

		result.Escaped.Should().BeFalse();
		result.Line.Should().Be("hello");
		harness.RawOutput.Should().Contain("Matches: hello, help");
		harness.RawOutput.Should().Contain("hello");
		harness.Frames.Should().Contain(frame => frame.Lines.Any(line => line.Contains("> h", StringComparison.Ordinal)));
		harness.CursorX.Should().Be(0);
		harness.GetVisibleLines().Any(line => line.Contains("Cohelp", StringComparison.OrdinalIgnoreCase)).Should().BeFalse();
	}

	[TestMethod]
	[Description("Menu refresh uses rendered terminal frames: when suggestions narrow after typing, stale candidates disappear from the viewport.")]
	public async Task When_MenuRefreshNarrowsSuggestions_Then_ViewportStopsShowingStaleCandidate()
	{
		var harness = new TerminalHarness(cols: 52, rows: 8);
		var keyReader = new FakeKeyReader([Key(ConsoleKey.H, 'h'), Key(ConsoleKey.Tab, '\t'), Key(ConsoleKey.Tab, '\t'), Key(ConsoleKey.E, 'e'), Key(ConsoleKey.Enter, '\r'), Key(ConsoleKey.Enter, '\r')]);
		var result = await RunReadAsync(harness, keyReader, DynamicResolverAsync, primeBottom: false).ConfigureAwait(false);

		result.Line.Should().StartWith("hello");
		var frameWithBoth = harness.Frames.FirstOrDefault(frame => frame.Lines.Any(line => line.Contains("Matches: hello, help", StringComparison.Ordinal)));
		frameWithBoth.Should().NotBeNull();
		var frameWithNarrowed = harness.Frames.LastOrDefault(frame => frame.Lines.Any(line => line.Contains("Matches: hello", StringComparison.Ordinal)));
		frameWithNarrowed.Should().NotBeNull();
		frameWithNarrowed!.Lines.Any(line => line.Contains("Matches: hello, help", StringComparison.Ordinal)).Should().BeFalse();
	}

	private static async Task<ConsoleLineReader.ReadResult> RunReadAsync(
		TerminalHarness harness,
		FakeKeyReader keyReader,
		ConsoleLineReader.AutocompleteResolver resolver,
		bool primeBottom)
	{
		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (harness.Cols, harness.Rows);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;
			if (primeBottom)
			{
				await WritePromptAtBottomAsync(harness).ConfigureAwait(false);
			}

			return await ConsoleLineReader.ReadLineAsync(
					history: null,
					resolver,
					ConsoleLineReader.AutocompleteRenderMode.Rich,
					maxVisibleSuggestions: 6,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: true,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static async Task WritePromptAtBottomAsync(TerminalHarness harness)
	{
		for (var i = 0; i < harness.Rows - 1; i++)
		{
			await ReplSessionIO.Output.WriteAsync($"line-{i}\r\n").ConfigureAwait(false);
		}

		await ReplSessionIO.Output.WriteAsync("> ").ConfigureAwait(false);
		await ReplSessionIO.Output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
	}

	private static ValueTask<ConsoleLineReader.AutocompleteResult?> StaticResolverAsync(
		ConsoleLineReader.AutocompleteRequest request,
		CancellationToken _)
	{
		return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
			new ConsoleLineReader.AutocompleteResult(
				ReplaceStart: 0,
				ReplaceLength: request.Input.Length,
				Suggestions:
				[
					new ConsoleLineReader.AutocompleteSuggestion("hello", Description: "Greeting command"),
					new ConsoleLineReader.AutocompleteSuggestion("help", Description: "Help command"),
				],
				HintLine: "Matches: hello, help"));
	}

	private static ValueTask<ConsoleLineReader.AutocompleteResult?> DynamicResolverAsync(
		ConsoleLineReader.AutocompleteRequest request,
		CancellationToken _)
	{
		var suggestions = request.Input.StartsWith("he", StringComparison.OrdinalIgnoreCase)
			? new[]
			{
				new ConsoleLineReader.AutocompleteSuggestion("hello", Description: "Greeting command"),
			}
			: new[]
			{
				new ConsoleLineReader.AutocompleteSuggestion("hello", Description: "Greeting command"),
				new ConsoleLineReader.AutocompleteSuggestion("help", Description: "Help command"),
			};
		return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
			new ConsoleLineReader.AutocompleteResult(
				ReplaceStart: 0,
				ReplaceLength: request.Input.Length,
				Suggestions: suggestions,
				HintLine: $"Matches: {string.Join(", ", suggestions.Select(static item => item.Value))}"));
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
