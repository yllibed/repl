using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ConsoleLineReader_Autocomplete
{
	[TestMethod]
	[Description("First Tab extends the current token to the common prefix of candidates.")]
	public async Task When_FirstTabPressed_Then_CommonPrefixIsApplied()
	{
		var harness = new TerminalHarness(cols: 50, rows: 8);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		static ValueTask<ConsoleLineReader.AutocompleteResult?> ResolverAsync(
			ConsoleLineReader.AutocompleteRequest request,
			CancellationToken _)
		{
			request.Input.Should().Be("he");
			request.Cursor.Should().Be(2);
			return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
				new ConsoleLineReader.AutocompleteResult(
					ReplaceStart: 0,
					ReplaceLength: 2,
					Suggestions:
					[
						new ConsoleLineReader.AutocompleteSuggestion("hello"),
						new ConsoleLineReader.AutocompleteSuggestion("help"),
					]));
		}

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			var result = await ConsoleLineReader.ReadLineAsync(
					history: null,
					ResolverAsync,
					ConsoleLineReader.AutocompleteRenderMode.Basic,
					maxVisibleSuggestions: 8,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: false,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);

			result.Escaped.Should().BeFalse();
			result.Line.Should().Be("hel");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Second Tab opens list rendering and Enter accepts selected candidate before final submit.")]
	public async Task When_SecondTabPressed_Then_MenuIsRenderedAndSelectionIsApplied()
	{
		var harness = new TerminalHarness(cols: 60, rows: 10);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		static ValueTask<ConsoleLineReader.AutocompleteResult?> ResolverAsync(
			ConsoleLineReader.AutocompleteRequest request,
			CancellationToken _)
		{
			return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
				new ConsoleLineReader.AutocompleteResult(
					ReplaceStart: 0,
					ReplaceLength: request.Input.Length,
					Suggestions:
					[
						new ConsoleLineReader.AutocompleteSuggestion("hello"),
						new ConsoleLineReader.AutocompleteSuggestion("help"),
					]));
		}

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			var result = await ConsoleLineReader.ReadLineAsync(
					history: null,
					ResolverAsync,
					ConsoleLineReader.AutocompleteRenderMode.Basic,
					maxVisibleSuggestions: 8,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: false,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);

			result.Line.Should().Be("hello");
			harness.RawOutput.Should().MatchRegex(@"(?m)^\s*>\s*hello\s*$");
			harness.RawOutput.Should().MatchRegex(@"(?m)^\s*help\s*$");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Down arrow in open menu changes selected candidate and Enter accepts it.")]
	public async Task When_MenuSelectionMovesDown_Then_SelectedCandidateIsCommitted()
	{
		var harness = new TerminalHarness(cols: 60, rows: 10);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.DownArrow),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		static ValueTask<ConsoleLineReader.AutocompleteResult?> ResolverAsync(
			ConsoleLineReader.AutocompleteRequest request,
			CancellationToken _)
		{
			return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
				new ConsoleLineReader.AutocompleteResult(
					ReplaceStart: 0,
					ReplaceLength: request.Input.Length,
					Suggestions:
					[
						new ConsoleLineReader.AutocompleteSuggestion("hello"),
						new ConsoleLineReader.AutocompleteSuggestion("help"),
					]));
		}

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			var result = await ConsoleLineReader.ReadLineAsync(
					history: null,
					ResolverAsync,
					ConsoleLineReader.AutocompleteRenderMode.Basic,
					maxVisibleSuggestions: 8,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: false,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);

			result.Line.Should().Be("help");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("When menu is open and user keeps typing, resolver is called again with menu requested.")]
	public async Task When_MenuIsOpenAndTyping_Then_AssistIsRefreshed()
	{
		var harness = new TerminalHarness(cols: 60, rows: 10);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.E, 'e'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
		]);
		var requests = new List<ConsoleLineReader.AutocompleteRequest>();

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (60, 10);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var result = await ConsoleLineReader.ReadLineAsync(
					history: null,
					(request, ct) => ResolveDynamicMenuRefreshAsync(requests, request, ct),
					ConsoleLineReader.AutocompleteRenderMode.Rich,
					maxVisibleSuggestions: 8,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: false,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);

			result.Line.Should().StartWith("hello");
			requests.Should().Contain(request => request.MenuRequested);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("When no overlay rows are available, rich autocomplete falls back to scroll and still renders assist content.")]
	public async Task When_NoOverlayRowsAvailable_Then_RichAssistStillRendersUsingScroll()
	{
		var harness = new TerminalHarness(cols: 60, rows: 10);
		var keyReader = new FakeKeyReader(
		[
			Key(ConsoleKey.H, 'h'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Tab, '\t'),
			Key(ConsoleKey.Enter, '\r'),
			Key(ConsoleKey.Enter, '\r'),
		]);

		static ValueTask<ConsoleLineReader.AutocompleteResult?> ResolverAsync(
			ConsoleLineReader.AutocompleteRequest request,
			CancellationToken _)
		{
			return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
				new ConsoleLineReader.AutocompleteResult(
					ReplaceStart: 0,
					ReplaceLength: request.Input.Length,
					Suggestions:
					[
						new ConsoleLineReader.AutocompleteSuggestion("hello"),
						new ConsoleLineReader.AutocompleteSuggestion("help"),
					],
					HintLine: "Matches: hello, help"));
		}

		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		using var overlayRows = ConsoleLineReader.OverrideAvailableOverlayRowsForTests(rows: 0);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (60, 10);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;

			var result = await ConsoleLineReader.ReadLineAsync(
					history: null,
					ResolverAsync,
					ConsoleLineReader.AutocompleteRenderMode.Rich,
					maxVisibleSuggestions: 8,
					AutocompletePresentation.Hybrid,
					liveHintEnabled: true,
					colorizeInputLine: false,
					colorizeHintAndMenu: false,
					ConsoleLineReader.AutocompleteColorStyles.Empty,
					CancellationToken.None)
				.ConfigureAwait(false);

			result.Line.Should().Be("hello");
			harness.RawOutput.Should().Contain("Matches: hello, help");
			harness.RawOutput.Should().MatchRegex(@"\u001b\[38;5;110m>\u001b\[0m\s+hello");
			harness.RawOutput.Should().Contain("help");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static ValueTask<ConsoleLineReader.AutocompleteResult?> ResolveDynamicMenuRefreshAsync(
		List<ConsoleLineReader.AutocompleteRequest> requests,
		ConsoleLineReader.AutocompleteRequest request,
		CancellationToken _)
	{
		requests.Add(request);
		var suggestions = request.Input.StartsWith("hell", StringComparison.OrdinalIgnoreCase)
			? new[]
			{
				new ConsoleLineReader.AutocompleteSuggestion("hello"),
			}
			: new[]
			{
				new ConsoleLineReader.AutocompleteSuggestion("hello"),
				new ConsoleLineReader.AutocompleteSuggestion("help"),
			};
		return ValueTask.FromResult<ConsoleLineReader.AutocompleteResult?>(
			new ConsoleLineReader.AutocompleteResult(
				ReplaceStart: 0,
				ReplaceLength: request.Input.Length,
				Suggestions: suggestions));
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
		new(ch, key, shift: false, alt: false, control: false);
}
