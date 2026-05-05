using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ResultFlowPager
{
	[TestMethod]
	[Description("Result-flow pager advances by page on Space and stops on Q.")]
	public async Task When_PagingWithSpaceAndQuit_Then_WritesOnlyRequestedPages()
	{
		var writer = new StringWriter();
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
		output.Should().Contain("Up/PageUp: back");
		output.Should().Contain("q/Esc: stop");
	}

	[TestMethod]
	[Description("Result-flow pager advances by one line on Enter.")]
	public async Task When_PagingWithEnter_Then_AdvancesSingleLine()
	{
		var writer = new StringWriter();
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
	[Description("Result-flow pager UpArrow moves back one line instead of jumping to the header.")]
	public async Task When_PagingBackWithUpArrow_Then_DoesNotRepeatHeader()
	{
		var writer = new StringWriter();
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.UpArrow, '\0'),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"# At Area Event Summary\nr1\nr2\nr3\nr4\nr5",
			writer,
			keys,
			visibleRows: 2,
			CancellationToken.None);

		var output = writer.ToString();
		output.Split("# At Area Event Summary", StringSplitOptions.None)
			.Should().HaveCount(2);
		output.Should().Contain("r1");
		output.Should().Contain("r2");
		output.Should().Contain("r3");
	}

	[TestMethod]
	[Description("Result-flow pager fetches the next data page in the same interactive run.")]
	public async Task When_CurrentPayloadEndsAndMoreDataExists_Then_SpaceFetchesNextPayload()
	{
		var writer = new StringWriter();
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
	[Description("Result-flow pager stops at a data-page boundary without fetching more data when the user quits.")]
	public async Task When_CurrentPayloadEndsAndUserQuits_Then_DoesNotFetchNextPayload()
	{
		var writer = new StringWriter();
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
		var writer = new StringWriter();
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
	[Description("Result-flow pager replays the previous full window when the user presses UpArrow at a data-page boundary.")]
	public async Task When_AtPayloadBoundaryAndUserPressesUpArrow_Then_ReplaysPreviousWindow()
	{
		var writer = new StringWriter();
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
		output.Split("three", StringSplitOptions.None).Should().HaveCount(3);
		output.Split("four", StringSplitOptions.None).Should().HaveCount(3);
	}

	[TestMethod]
	[Description("Result-flow scroll pager owns an alternate-screen viewport instead of relying on terminal scrollback.")]
	public async Task When_ScrollPagerRunsWithAnsi_Then_UsesAlternateScreenViewport()
	{
		var writer = new StringWriter();
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
			pagerMode: ReplPagerMode.Scroll,
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
	[Description("Result-flow scroll pager fetches additional payloads into the same viewport when the user pages past the buffered end.")]
	public async Task When_ScrollPagerReachesBufferedEnd_Then_FetchesNextPayload()
	{
		var writer = new StringWriter();
		var fetches = 0;
		var keys = new FakeKeyReader(
		[
			MakeKey(ConsoleKey.Spacebar, ' '),
			MakeKey(ConsoleKey.Q, 'q'),
		]);

		await ResultFlowPager.WriteAsync(
			"one\ntwo",
			writer,
			keys,
			visibleRows: 3,
			pagerMode: ReplPagerMode.Scroll,
			ansiEnabled: true,
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
		output.Should().Contain("four");
		output.Should().Contain("\u001b[?1049h");
	}

	[TestMethod]
	[Description("Result-flow scroll pager advances to the new buffered end when a fetch returns fewer lines than one viewport.")]
	public async Task When_ScrollPagerFetchesShortPayload_Then_ViewportAdvances()
	{
		var writer = new StringWriter();
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
			pagerMode: ReplPagerMode.Scroll,
			ansiEnabled: true,
			hasMorePayload: true,
			fetchNextPayload: _ => ValueTask.FromResult<ResultFlowPagerPage?>(
				new ResultFlowPagerPage("four", HasMore: false)),
			CancellationToken.None);

		var output = writer.ToString();
		output.Should().Contain("2-4/4");
		output.Should().Contain("four");
	}

	private static ConsoleKeyInfo MakeKey(ConsoleKey key, char keyChar) =>
		new(keyChar, key, shift: false, alt: false, control: false);
}
