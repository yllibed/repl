namespace Repl;

internal static class ResultFlowPager
{
	private const string MorePrompt = "--More-- Space/PageDown: continue, Enter/Down: line, Up/PageUp: back, q/Esc: stop";
	private const string EnterAlternateScreen = "\u001b[?1049h";
	private const string LeaveAlternateScreen = "\u001b[?1049l";
	private const string HideCursor = "\u001b[?25l";
	private const string ShowCursor = "\u001b[?25h";
	private const string CursorHome = "\u001b[H";
	private const string ClearToEndOfScreen = "\u001b[J";

	public static int CountLines(string payload) => SplitLines(payload).Length;

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		CancellationToken cancellationToken = default)
	{
		await WriteAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				hasMorePayload: false,
				fetchNextPayload: null,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		ReplPagerMode pagerMode,
		bool ansiEnabled,
		CancellationToken cancellationToken = default)
	{
		await WriteAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				pagerMode,
				ansiEnabled,
				hasMorePayload: false,
				fetchNextPayload: null,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken = default)
	{
		await WriteAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				ReplPagerMode.More,
				ansiEnabled: false,
				hasMorePayload,
				fetchNextPayload,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		ReplPagerMode pagerMode,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);

		if (ShouldUseScrollPager(pagerMode, ansiEnabled))
		{
			await WriteScrollAsync(
					payload,
					output,
					keyReader,
					visibleRows,
					ansiEnabled,
					hasMorePayload,
					fetchNextPayload,
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		await WriteMoreAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				hasMorePayload,
				fetchNextPayload,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private static async ValueTask WriteMoreAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		var state = new PagerState(SplitLines(payload), Math.Max(1, visibleRows), hasMorePayload);
		if (state.Lines.Length == 0 && !state.HasMorePayload)
		{
			return;
		}

		while (true)
		{
			if (state.Lines.Length == 0 && state.HasMorePayload && fetchNextPayload is not null)
			{
				var payloadPage = await fetchNextPayload(cancellationToken).ConfigureAwait(false);
				if (payloadPage is null)
				{
					return;
				}

				state.Reset(SplitLines(payloadPage.Payload), payloadPage.HasMore);
				continue;
			}

			if (await WriteCurrentPayloadAsync(state, output, keyReader, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			if (!state.HasMorePayload || fetchNextPayload is null)
			{
				break;
			}

			var boundaryKey = await ReadPromptAsync(output, keyReader, cancellationToken).ConfigureAwait(false);
			if (ApplyBoundaryKey(state, boundaryKey))
			{
				return;
			}

			if (state.Index < state.Lines.Length)
			{
				continue;
			}

			var nextPayload = await fetchNextPayload(cancellationToken).ConfigureAwait(false);
			if (nextPayload is null)
			{
				break;
			}

			state.Reset(SplitLines(nextPayload.Payload), nextPayload.HasMore);
		}
	}

	private static async ValueTask WriteScrollAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		if (!ansiEnabled)
		{
			throw new InvalidOperationException("The scroll result pager requires ANSI support.");
		}

		var state = new ScrollPagerState(SplitLines(payload), Math.Max(2, visibleRows), hasMorePayload);
		if (state.Buffer.Count == 0 && !state.HasMorePayload)
		{
			return;
		}

		await output.WriteAsync(EnterAlternateScreen).ConfigureAwait(false);
		await output.WriteAsync(HideCursor).ConfigureAwait(false);
		try
		{
			await EnsureScrollBufferAsync(state, fetchNextPayload, cancellationToken).ConfigureAwait(false);
			while (true)
			{
				await RenderScrollAsync(state, output, cancellationToken).ConfigureAwait(false);
				var key = await keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
				if (ApplyScrollKey(state, key))
				{
					return;
				}

				if (state.HasReachedBottom
					&& state.Buffer.Count > state.ViewportHeight
					&& state.HasMorePayload
					&& fetchNextPayload is not null)
				{
					var before = state.Buffer.Count;
					await FetchIntoScrollBufferAsync(state, fetchNextPayload, cancellationToken).ConfigureAwait(false);
					if (state.Buffer.Count > before)
					{
						state.TopLine = Math.Min(state.TopLine + state.ViewportHeight, state.MaxTopLine);
					}
				}
			}
		}
		finally
		{
			await output.WriteAsync(ShowCursor).ConfigureAwait(false);
			await output.WriteAsync(LeaveAlternateScreen).ConfigureAwait(false);
			await output.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private static async ValueTask<bool> WriteCurrentPayloadAsync(
		PagerState state,
		TextWriter output,
		IReplKeyReader keyReader,
		CancellationToken cancellationToken)
	{
		while (state.Index < state.Lines.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var windowStart = state.Index;
			var take = Math.Min(state.NextWindow, state.Lines.Length - state.Index);
			for (var i = 0; i < take; i++)
			{
				await output.WriteLineAsync(state.Lines[state.Index + i]).ConfigureAwait(false);
			}

			state.Index += take;
			if (state.Index >= state.Lines.Length)
			{
				break;
			}

			var key = await ReadPromptAsync(output, keyReader, cancellationToken).ConfigureAwait(false);
			if (ApplyWindowKey(state, key, windowStart))
			{
				return true;
			}
		}

		return false;
	}

	private static bool ApplyWindowKey(PagerState state, ConsoleKeyInfo key, int windowStart)
	{
		switch (key.Key)
		{
			case ConsoleKey.Q:
			case ConsoleKey.Escape:
				return true;
			case ConsoleKey.Enter:
			case ConsoleKey.DownArrow:
				state.NextWindow = 1;
				return false;
			case ConsoleKey.UpArrow:
				state.Index = Math.Max(0, windowStart - 1);
				state.NextWindow = 1;
				return false;
			case ConsoleKey.PageUp:
				state.Index = Math.Max(0, windowStart - state.PageSize);
				state.NextWindow = state.PageSize;
				return false;
			default:
				state.NextWindow = state.PageSize;
				return false;
		}
	}

	private static bool ApplyBoundaryKey(PagerState state, ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Q:
			case ConsoleKey.Escape:
				return true;
			case ConsoleKey.Enter:
			case ConsoleKey.DownArrow:
				state.NextWindow = 1;
				return false;
			case ConsoleKey.UpArrow:
				state.Index = Math.Max(0, state.Lines.Length - state.PageSize);
				state.NextWindow = state.PageSize;
				return false;
			case ConsoleKey.PageUp:
				state.Index = Math.Max(0, state.Lines.Length - state.PageSize);
				state.NextWindow = state.PageSize;
				return false;
			default:
				state.NextWindow = state.PageSize;
				return false;
		}
	}

	private static async ValueTask<ConsoleKeyInfo> ReadPromptAsync(
		TextWriter output,
		IReplKeyReader keyReader,
		CancellationToken cancellationToken)
	{
		await output.WriteAsync(MorePrompt).ConfigureAwait(false);
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
		var key = await keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
		await output.WriteLineAsync().ConfigureAwait(false);
		return key;
	}

	private static async ValueTask EnsureScrollBufferAsync(
		ScrollPagerState state,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		while (state.Buffer.Count == 0 && state.HasMorePayload && fetchNextPayload is not null)
		{
			await FetchIntoScrollBufferAsync(state, fetchNextPayload, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async ValueTask FetchIntoScrollBufferAsync(
		ScrollPagerState state,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>> fetchNextPayload,
		CancellationToken cancellationToken)
	{
		var nextPayload = await fetchNextPayload(cancellationToken).ConfigureAwait(false);
		if (nextPayload is null)
		{
			state.HasMorePayload = false;
			return;
		}

		state.Append(SplitLines(nextPayload.Payload), nextPayload.HasMore);
	}

	private static async ValueTask RenderScrollAsync(
		ScrollPagerState state,
		TextWriter output,
		CancellationToken cancellationToken)
	{
		await output.WriteAsync(CursorHome).ConfigureAwait(false);
		await output.WriteAsync(ClearToEndOfScreen).ConfigureAwait(false);
		var take = Math.Min(state.ViewportHeight, Math.Max(0, state.Buffer.Count - state.TopLine));
		for (var i = 0; i < take; i++)
		{
			await output.WriteLineAsync(state.Buffer[state.TopLine + i]).ConfigureAwait(false);
		}

		for (var i = take; i < state.ViewportHeight; i++)
		{
			await output.WriteLineAsync().ConfigureAwait(false);
		}

		var lastLine = state.Buffer.Count == 0
			? 0
			: Math.Min(state.Buffer.Count, state.TopLine + state.ViewportHeight);
		var status = state.Buffer.Count == 0
			? "-- result-flow: loading --"
			: $"-- result-flow {state.TopLine + 1}-{lastLine}/{state.Buffer.Count}{(state.HasMorePayload ? "+" : string.Empty)}  Space: next  Up/Down: scroll  q: quit --";
		await output.WriteAsync(status).ConfigureAwait(false);
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static bool ApplyScrollKey(ScrollPagerState state, ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Q:
			case ConsoleKey.Escape:
				return true;
			case ConsoleKey.DownArrow:
			case ConsoleKey.J:
				state.TopLine = Math.Min(state.TopLine + 1, state.MaxTopLine);
				return false;
			case ConsoleKey.UpArrow:
			case ConsoleKey.K:
				state.TopLine = Math.Max(0, state.TopLine - 1);
				return false;
			case ConsoleKey.PageUp:
			case ConsoleKey.B:
				state.TopLine = Math.Max(0, state.TopLine - state.ViewportHeight);
				return false;
			case ConsoleKey.Home:
			case ConsoleKey.G when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
				state.TopLine = 0;
				return false;
			default:
				state.TopLine = Math.Min(state.TopLine + state.ViewportHeight, state.MaxTopLine);
				return false;
		}
	}

	private static bool ShouldUseScrollPager(ReplPagerMode pagerMode, bool ansiEnabled) =>
		ansiEnabled && pagerMode is ReplPagerMode.Auto or ReplPagerMode.Scroll;

	private static string[] SplitLines(string payload) =>
		string.IsNullOrEmpty(payload)
			? []
			: SplitNonEmptyPayloadLines(payload);

	private static string[] SplitNonEmptyPayloadLines(string payload)
	{
		var lines = new List<string>();
		var start = 0;
		for (var index = 0; index < payload.Length; index++)
		{
			var current = payload[index];
			if (current is not '\r' and not '\n')
			{
				continue;
			}

			lines.Add(payload[start..index]);
			if (current == '\r' && index + 1 < payload.Length && payload[index + 1] == '\n')
			{
				index++;
			}

			start = index + 1;
		}

		if (start < payload.Length)
		{
			lines.Add(payload[start..]);
		}

		return [.. lines];
	}

	private sealed class PagerState(string[] lines, int pageSize, bool hasMorePayload)
	{
		public string[] Lines { get; private set; } = lines;

		public int PageSize { get; } = pageSize;

		public int NextWindow { get; set; } = pageSize;

		public int Index { get; set; }

		public bool HasMorePayload { get; private set; } = hasMorePayload;

		public void Reset(string[] lines, bool hasMorePayload)
		{
			Lines = lines;
			Index = 0;
			HasMorePayload = hasMorePayload;
		}
	}

	private sealed class ScrollPagerState(string[] lines, int visibleRows, bool hasMorePayload)
	{
		public List<string> Buffer { get; } = [.. lines];

		public int ViewportHeight { get; } = Math.Max(1, visibleRows - 1);

		public int TopLine { get; set; }

		public bool HasMorePayload { get; set; } = hasMorePayload;

		public int MaxTopLine => Math.Max(0, Buffer.Count - ViewportHeight);

		public bool HasReachedBottom => TopLine >= MaxTopLine;

		public void Append(string[] lines, bool hasMorePayload)
		{
			Buffer.AddRange(lines);
			HasMorePayload = hasMorePayload;
		}
	}
}
