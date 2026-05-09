using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ResultFlowPager
{
	[TestMethod]
	[Description("Result-flow pager advances by page on Space and stops on Q.")]
	public async Task When_PagingWithSpaceAndQuit_Then_WritesOnlyRequestedPages()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour\nfive",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().Contain("four");
		output.Should().NotContain("five");
		output.Should().Contain("--More--");
		output.Should().Contain("Space/PageDown: continue");
		output.Should().Contain("Enter/Down: line");
		output.Should().Contain("Up/PageUp: ignored");
		output.Should().Contain("q/Esc: stop");
	}

	[TestMethod]
	[Description("Result-flow pager advances by one line on Enter.")]
	public async Task When_PagingWithEnter_Then_AdvancesSingleLine()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Enter, '\r'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().NotContain("four");
	}

	[TestMethod]
	[Description("Result-flow more pager clears the prompt before writing the next row when ANSI rendering is available.")]
	public async Task When_MorePagerUsesAnsiPrompt_Then_PromptDoesNotBecomeAVisibleRow()
	{
		using var writer = new StringWriter();
		const string morePrompt = "--More-- Space/PageDown: continue, Enter/Down: line, Up/PageUp: ignored, q/Esc: stop";
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 2,
			pagerMode: ReplPagerMode.More,
			ansiEnabled: true,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain($"two{Environment.NewLine}{morePrompt}");
		output.Should().Contain($"{morePrompt}\r{new string(' ', morePrompt.Length)}\rthree");
		output.Should().Contain($"{morePrompt}\r{new string(' ', morePrompt.Length)}\rfour");
		output.Should().NotContain($"{morePrompt}{Environment.NewLine}three");
		output.Should().NotContain($"{morePrompt}{Environment.NewLine}four");
	}

	[TestMethod]
	[Description("Result-flow more pager ignores UpArrow because append-only output cannot redraw previous lines cleanly.")]
	public async Task When_MorePagerReceivesUpArrow_Then_DoesNotReplayOrAdvance()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.UpArrow, '\0'),
			MakeKey(ConsoleKey.UpArrow, '\0'),
			MakeKey(ConsoleKey.PageUp, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"# At Area Event Summary\n---\nr1\nr2\nr3\nr4\nr5",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Split("# At Area Event Summary", StringSplitOptions.None).Should().HaveCount(2);
		output.Split("--More--", StringSplitOptions.None).Should().HaveCount(2);
		output.Should().Contain("r1");
		output.Should().Contain("r2");
		output.Should().NotContain("r3");
	}

	[TestMethod]
	[Description("Result-flow pager fetches the next data page in the same interactive run.")]
	public async Task When_CurrentPayloadEndsAndMoreDataExists_Then_SpaceFetchesNextPayload()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("three\nfour", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(1);
		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().Contain("four");
	}

	[TestMethod]
	[Description("Result-flow more pager fills the requested PageDown window across payload boundaries.")]
	public async Task When_MorePagerPageDownCrossesPayloadBoundary_Then_FetchesAndContinuesWindow()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.PageDown, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("four\nfive\nsix", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().Contain("three");
		output.Should().Contain("four");
		output.Should().NotContain("five");
		output.Should().NotContain("six");
	}

	[TestMethod]
	[Description("Result-flow pager stops at a data-page boundary without fetching more data when the user quits.")]
	public async Task When_CurrentPayloadEndsAndUserQuits_Then_DoesNotFetchNextPayload()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("three\nfour", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(0);
		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().NotContain("three");
		output.Should().NotContain("four");
	}

	[TestMethod]
	[Description("Result-flow pager fetches the next data page instead of showing an empty --More-- prompt when a payload has no content.")]
	public async Task When_CurrentPayloadIsEmptyAndMoreDataExists_Then_FetchesNextPayload()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader([]);

		await ResultFlowPager.WriteAsync(
			string.Empty,
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("one\ntwo", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(1);
		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("two");
		output.Should().NotContain("--More--");
	}

	[TestMethod]
	[Description("Result-flow more pager ignores UpArrow at a payload boundary instead of replaying previous lines.")]
	public async Task When_MorePagerAtPayloadBoundaryReceivesUpArrow_Then_DoesNotReplayOrFetch()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.UpArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ => throw new InvalidOperationException("Should not fetch while replaying the previous window."),
			CancellationToken.None);

		var output = writer.ToString();
		output.Split("three", StringSplitOptions.None).Should().HaveCount(2);
		output.Split("four", StringSplitOptions.None).Should().HaveCount(2);
	}

	[TestMethod]
	[Description("Result-flow more pager strips duplicate page headers and page footers from fetched payloads.")]
	public async Task When_MorePagerFetchesNextPayload_Then_DuplicateHeadersAndFootersAreSkipped()
	{
		using var writer = new StringWriter();
		var header = "#    At";
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
		]);

		await ResultFlowPager.WriteAsync(
			$"{header}\none\ntwo\nShowing 2 of 5.",
			writer,
			keys,
			visibleRows: 4,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage($"{header}\nthree\nShowing 1 of 5.", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Split(header, StringSplitOptions.None).Should().HaveCount(2);
		output.Should().Contain("three");
		output.Should().NotContain("Showing 2 of 5");
		output.Should().NotContain("Showing 1 of 5");
	}

	[TestMethod]
	[Description("Result-flow more pager treats plain human-output column headings as headers and strips duplicates from fetched pages.")]
	public async Task When_MorePagerFetchesHumanOutput_Then_DuplicateHashHeadersAreSkipped()
	{
		using var writer = new StringWriter();
		var header = "#    At              Area       Event      Summary";
		var fetchedHeader = "#   At                 Area       Event       Summary";
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
		]);

		await ResultFlowPager.WriteAsync(
			$"{header}\n---  -----------------  ---------  ----------  ----------------------------------------\n1    2026-01-12      identity   validated  identity batch 1 validated successfully\nShowing 1 of 3.",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage(
					$"{fetchedHeader}\n---  -----------------  ---------  ----------  ----------------------------------------\n2    2026-01-12      billing    queued     billing batch 1 queued successfully\nShowing 2 of 3.",
					HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Split(header, StringSplitOptions.None).Should().HaveCount(2);
		output.Should().NotContain(fetchedHeader);
		output.Should().Contain("identity batch 1 validated successfully");
		output.Should().Contain("billing batch 1 queued successfully");
		output.Should().NotContain("Showing 1 of 3");
		output.Should().NotContain("Showing 2 of 3");
	}

	[TestMethod]
	[Description("Structured continuation payloads do not use presentation-text sniffing, so data lines that look like footers are preserved.")]
	public async Task When_ContinuationPayloadIsMarkedClean_Then_FooterLikeDataLineIsPreserved()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one",
			writer,
			keys,
			visibleRows: 2,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("Showing 1 of 5.", HasMore: false, ContainsPresentationChrome: false)),
			CancellationToken.None);

		writer.ToString().Should().Contain("Showing 1 of 5.");
	}

	[TestMethod]
	[Description("Result-flow full pager owns an alternate-screen viewport instead of relying on terminal scrollback.")]
	public async Task When_ScrollPagerRunsWithAnsi_Then_UsesAlternateScreenViewport()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("\u001b[?1049h");
		output.Should().Contain("\u001b[?1049l");
		output.Should().Contain("\u001b[H\u001b[J");
		output.Should().Contain("one");
		output.Should().Contain("three");
		output.Should().Contain("q: quit");
		output.Should().NotContain("--More--");
	}

	[TestMethod]
	[Description("Result-flow full pager fetches additional payloads into the same viewport when the user pages past the buffered end.")]
	public async Task When_ScrollPagerReachesBufferedEnd_Then_FetchesNextPayload()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("four\nfive", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(1);
		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("four");
		output.Should().Contain("\u001b[?1049h");
	}

	[TestMethod]
	[Description("Result-flow full pager advances to the new buffered end when a fetch returns fewer lines than one viewport.")]
	public async Task When_ScrollPagerFetchesShortPayload_Then_ViewportAdvances()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 4,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("five", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("3-5/5");
		output.Should().Contain("five");
	}

	[TestMethod]
	[Description("Result-flow full pager fetches another payload when the current payload exactly fills the viewport and the user presses Space.")]
	public async Task When_ScrollPagerContentExactlyFitsViewport_Then_SpaceFetchesNextPayload()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 4,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("four", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(1);
		writer.ToString().Should().Contain("four");
	}

	[TestMethod]
	[Description("Result-flow pager does not add a phantom empty line when a payload ends with a newline.")]
	public async Task When_PayloadEndsWithNewline_Then_LineCountExcludesTrailingEmptyLine()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\n",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			CancellationToken.None);

		writer.ToString().Should().Contain("1-2/2");
	}

	[TestMethod]
	[Description("Result-flow full pager treats unrecognized keys as no-ops and does not advance the viewport or trigger a fetch.")]
	public async Task When_ScrollPagerUnknownKeyPressed_Then_ViewportDoesNotAdvanceAndNoFetch()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.F1, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("five", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(0);
		writer.ToString().Should().NotContain("five");
	}

	[TestMethod]
	[Description("Result-flow full pager advances the viewport only on Space/PageDown, not on Enter or other keys.")]
	public async Task When_ScrollPagerEnterKeyPressed_Then_ViewportAdvancesByOneLine()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Enter, '\r'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			CancellationToken.None);

		// Enter maps to DownArrow (one line); status bar should show 2-3/4, not 3-4/4
		writer.ToString().Should().Contain("2-3/4");
	}

	[TestMethod]
	[Description("Result-flow full pager advances by one line when Down fetches the next payload at a boundary.")]
	public async Task When_ScrollPagerDownFetchesNextPayload_Then_ViewportAdvancesOneLine()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("four\nfive", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("2-3/3+");
		output.Should().Contain("3-4/5");
		output.Should().NotContain("4-5/5");
	}

	[TestMethod]
	[Description("Result-flow full pager keeps a rich table header pinned and skips duplicate headers from later payloads.")]
	public async Task When_ScrollPagerHasRichTableHeader_Then_HeaderStaysPinned()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);
		var header = "\u001b[1m#\u001b[0m  \u001b[1mAt\u001b[0m";

		await ResultFlowPager.WriteAsync(
			$"{header}\none\ntwo\nthree",
			writer,
			keys,
			visibleRows: 4,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage($"{header}\nfour\nfive", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain(header);
		output.Should().Contain("three");
		output.Should().Contain("four");
		output.Should().Contain("3-4/5");
	}

	[TestMethod]
	[Description("Result-flow full pager does not clear the whole viewport on every redraw.")]
	public async Task When_ScrollPagerRedraws_Then_DoesNotClearScreenEveryTime()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			CancellationToken.None);

		writer.ToString().Split("\u001b[J").Length.Should().Be(2);
		writer.ToString().Should().NotContain("\u001b[2K");
	}

	[TestMethod]
	[Description("Result-flow full pager strips page footer hints already represented by its own status bar.")]
	public async Task When_ScrollPagerReceivesPageFooterLines_Then_FooterLinesAreNotRendered()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nShowing 2 of 5. Next data page: rerun with --result:cursor page-2.",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("three\nShowing 1 of 5.", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("one");
		output.Should().Contain("three");
		output.Should().NotContain("Showing 2 of 5");
		output.Should().NotContain("Showing 1 of 5");
	}

	[TestMethod]
	[Description("Result-flow full pager skips duplicate rich table headers even when they are not the first line in a fetched payload.")]
	public async Task When_ScrollPagerReceivesIndentedDuplicateHeader_Then_HeaderIsNotBuffered()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);
		var header = "\u001b[1m#\u001b[0m   \u001b[1mAt\u001b[0m";

		await ResultFlowPager.WriteAsync(
			$"{header}\none\ntwo\nShowing 2 of 5.",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage($"Showing 1 of 5.\n{header}\nthree", HasMore: false)),
			CancellationToken.None);

		writer.ToString().Should().Contain(header);
		writer.ToString().Should().Contain("three");
		writer.ToString().Should().Contain("3-3/3");
	}

	[TestMethod]
	[Description("Result-flow full pager End moves to the end of the currently buffered content.")]
	public async Task When_ScrollPagerEndPressed_Then_MovesToKnownEndWithoutFetching()
	{
		using var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.End, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour\nfive",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ =>
			{
				fetches++;
				return ValueTask.FromResult<ResultFlowPagerPage?>(
					new ResultFlowPagerPage("six", HasMore: false));
			},
			CancellationToken.None);

		fetches.Should().Be(0);
		writer.ToString().Should().Contain("4-5/5+");
	}

	[TestMethod]
	[Description("Result-flow full pager recalculates viewport height between redraws.")]
	public async Task When_ScrollPagerHeightChanges_Then_ViewportUsesCurrentHeight()
	{
		using var writer = new StringWriter();
		var visibleRows = 5;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);
		var reads = 0;

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour\nfive",
			writer,
			keys,
			visibleRows,
			visibleRowsProvider: () => reads++ == 0 ? visibleRows : 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: false,
			fetchNextPayload: null,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("1-4/5");
		output.Should().Contain("2-3/5");
		output.Should().Contain("\u001b[H\u001b[J");
	}

	[TestMethod]
	[Description("Result-flow full pager disables terminal line wrapping while the alternate screen is active.")]
	public async Task When_ScrollPagerRuns_Then_LineWrappingIsDisabledDuringAlternateScreen()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("\u001b[?7l");
		output.Should().Contain("\u001b[?7h");
		output.IndexOf("\u001b[?7l", StringComparison.Ordinal)
			.Should().BeLessThan(output.IndexOf("\u001b[?7h", StringComparison.Ordinal));
	}

	[TestMethod]
	[Description("Result-flow inline pager redraws in the main terminal buffer without entering the alternate screen.")]
	public async Task When_InlinePagerRuns_Then_RedrawsWithoutAlternateScreen()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.UpArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo\nthree\nfour",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Inline,
			ansiEnabled: true,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("\u001b[3A");
		output.Should().Contain("\u001b[J");
		output.Should().NotContain("\u001b[?1049h");
		output.Should().NotContain("\u001b[?1049l");
	}

	[TestMethod]
	[Description("Result-flow pager uses a configured custom renderer for the requested mode.")]
	public async Task When_CustomPagerRendererIsConfigured_Then_ItHandlesTheMatchingMode()
	{
		using var writer = new StringWriter();
		var renderer = new RecordingPagerRenderer(ReplPagerMode.Inline);

		await ResultFlowPager.WriteAsync(
			"one\ntwo",
			writer,
			new FakeKeyReader([]),
			visibleRows: 3,
			visibleRowsProvider: null,
			pagerMode: ReplPagerMode.Inline,
			ansiEnabled: true,
			hasMorePayload: false,
			fetchNextPayload: null,
			pagerRenderers: [renderer],
			CancellationToken.None);

		renderer.Payloads.Should().Equal("one\ntwo");
		writer.ToString().Should().Be("custom");
	}

	[TestMethod]
	[Description("Result-flow options expose pager renderers as a controlled read-only list keyed by mode.")]
	public void When_PagerRendererIsRegisteredTwiceForMode_Then_LatestRendererReplacesPrevious()
	{
		var options = new ResultFlowOptions();
		var first = new RecordingPagerRenderer(ReplPagerMode.Inline);
		var second = new RecordingPagerRenderer(ReplPagerMode.Inline);

		options.UsePagerRenderer(first);
		options.UsePagerRenderer(second);

		options.PagerRenderers.Should().ContainSingle().Which.Should().BeSameAs(second);
		options.RemovePagerRenderer(ReplPagerMode.Inline).Should().BeTrue();
		options.PagerRenderers.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Result-flow options reject inconsistent page-size bounds.")]
	public void When_MaxPageSizeIsSmallerThanDefault_Then_OptionsRejectIt()
	{
		var options = new ResultFlowOptions();

		var action = () => options.MaxPageSize = options.DefaultPageSize - 1;

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[TestMethod]
	[Description("ReplPagerRenderContext can be constructed and fetch payloads in custom renderer tests.")]
	public async Task When_RenderContextIsCreatedDirectly_Then_FetchNextPayloadReturnsConfiguredPayload()
	{
		var context = new ReplPagerRenderContext(
			initialPayload: "one",
			output: new StringWriter(),
			keyReader: new FakeKeyReader([]),
			visibleRows: 3,
			visibleRowsProvider: null,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ReplPagerPayload?>(new ReplPagerPayload("two", HasMore: false)));

		var payload = await context.FetchNextPayloadAsync(CancellationToken.None);

		payload.Should().NotBeNull();
		payload!.Payload.Should().Be("two");
		context.CanFetchNextPayload.Should().BeTrue();
	}

	[TestMethod]
	[Description("Viewport pager stops fetching and reports status when the buffered line limit is reached.")]
	public async Task When_ViewportPagerReachesBufferLimit_Then_StatusReportsLimit()
	{
		using var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.DownArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo",
			writer,
			keys,
			visibleRows: 3,
			visibleRowsProvider: null,
			pagerMode: ReplPagerMode.Full,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("three\nfour\nfive", HasMore: true)),
			pagerRenderers: null,
			maxBufferedLines: 3,
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("buffer limit");
		output.Should().Contain("three");
		output.Should().NotContain("four");
		output.Should().NotContain("five");
	}

	[TestMethod]
	[Description("PagerSession keeps its buffer capped and clears continuation when the cap is reached.")]
	public void When_PagerSessionReachesBufferLimit_Then_LinesStayReadOnlyAndHasMoreIsCleared()
	{
		var session = new PagerSession("one", hasMorePayload: true, maxBufferedLines: 2);

		session.Append("two\nthree", hasMorePayload: true);

		session.Lines.Should().Equal("one", "two");
		session.Lines.Should().NotBeAssignableTo<List<string>>();
		session.BufferLimitReached.Should().BeTrue();
		session.HasMorePayload.Should().BeFalse();
	}

	[TestMethod]
	[Description("Pager payload parser ignores malformed ANSI escape markers when normalizing headers.")]
	public void When_HeaderContainsLoneEscape_Then_NormalizationStillDeduplicatesContinuationHeader()
	{
		var header = "#   At  Area\u001b";
		var first = PagerPayloadParser.Parse($"{header}\none", header: null);
		var second = PagerPayloadParser.Parse($"{header}\ntwo", first.Header);

		second.ContentLines.Should().Equal("two");
	}

	private static ConsoleKeyInfo MakeKey(ConsoleKey key, char keyChar) =>
		new(keyChar, key, shift: false, alt: false, control: false);

	private sealed class RecordingPagerRenderer(ReplPagerMode mode) : IReplPagerRenderer
	{
		public List<string> Payloads { get; } = [];

		public ReplPagerMode Mode { get; } = mode;

		public async ValueTask RenderAsync(ReplPagerRenderContext context, CancellationToken cancellationToken = default)
		{
			Payloads.Add(context.InitialPayload);
			await context.Output.WriteAsync("custom").ConfigureAwait(false);
		}
	}
}
