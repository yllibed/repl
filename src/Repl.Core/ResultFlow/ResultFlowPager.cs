namespace Repl;

internal static class ResultFlowPager
{
	private const string MorePrompt = "--More-- Space/PageDown: continue, Enter/Down: line, Up/PageUp: ignored, q/Esc: stop";
	private const string FullStatus = "-- result-flow {0}-{1}/{2}{3}  Space: next  Up/Down: scroll  Home/End: known bounds  q: quit --";
	private const string FullStatusBufferLimit = "-- result-flow {0}-{1}/{2} buffer limit reached  Up/Down: scroll  q: quit --";
	private const int DefaultMaxBufferedLines = 10_000;
	private static readonly string MorePromptClear = new(' ', MorePrompt.Length);
	private static readonly string SpacePadding = new(' ', 256);
	private static readonly System.Text.CompositeFormat FullStatusFormat =
		System.Text.CompositeFormat.Parse(FullStatus);
	private static readonly System.Text.CompositeFormat FullStatusBufferLimitFormat =
		System.Text.CompositeFormat.Parse(FullStatusBufferLimit);

	public static int CountLines(string payload) => PagerPayloadParser.Parse(payload, header: null).TotalLineCount;

	public static ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		CancellationToken cancellationToken = default)
		=> WriteAsync(
			payload,
			output,
			keyReader,
			visibleRows,
			hasMorePayload: false,
			fetchNextPayload: null,
			cancellationToken);

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
				visibleRowsProvider: null,
				ReplPagerMode.More,
				ansiEnabled: false,
				hasMorePayload,
				fetchNextPayload,
				pagerRenderers: null,
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
		await WriteAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				visibleRowsProvider: null,
				pagerMode,
				ansiEnabled,
				hasMorePayload,
				fetchNextPayload,
				pagerRenderers: null,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int> visibleRowsProvider,
		ReplPagerMode pagerMode,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken = default)
	{
		await WriteAsync(
				payload,
				output,
				keyReader,
				visibleRows,
				visibleRowsProvider,
				pagerMode,
				ansiEnabled,
				hasMorePayload,
				fetchNextPayload,
				pagerRenderers: null,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public static ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		ReplPagerMode pagerMode,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		IEnumerable<IReplPagerRenderer>? pagerRenderers,
		CancellationToken cancellationToken = default)
		=> WriteAsync(
			payload,
			output,
			keyReader,
			visibleRows,
			visibleRowsProvider,
			pagerMode,
			ansiEnabled,
			hasMorePayload,
			fetchNextPayload,
			pagerRenderers,
			DefaultMaxBufferedLines,
			cancellationToken);

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		ReplPagerMode pagerMode,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		IEnumerable<IReplPagerRenderer>? pagerRenderers,
		int maxBufferedLines,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);
		maxBufferedLines = Math.Max(1, maxBufferedLines);

		var mode = ResolveMode(pagerMode, ansiEnabled);
		if (await TryRenderCustomAsync(
				mode,
				pagerRenderers,
				payload,
				output,
				keyReader,
				visibleRows,
				visibleRowsProvider,
				ansiEnabled,
				hasMorePayload,
				fetchNextPayload,
				cancellationToken)
			.ConfigureAwait(false))
		{
			return;
		}

		var session = new PagerSession(payload, hasMorePayload, maxBufferedLines);
		await RenderBuiltInAsync(
				mode,
				session,
				output,
				keyReader,
				visibleRows,
				visibleRowsProvider,
				ansiEnabled,
				fetchNextPayload,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private static async ValueTask RenderBuiltInAsync(
		ReplPagerMode mode,
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		bool ansiEnabled,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		switch (mode)
		{
			case ReplPagerMode.Full:
				await RenderViewportAsync(
						session,
						output,
						keyReader,
						Math.Max(2, visibleRows),
						visibleRowsProvider,
						fetchNextPayload,
						TerminalSurfaceMode.AlternateScreen,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			case ReplPagerMode.Inline:
				await RenderViewportAsync(
						session,
						output,
						keyReader,
						Math.Max(2, visibleRows),
						visibleRowsProvider,
						fetchNextPayload,
						TerminalSurfaceMode.InlineRegion,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				await RenderMoreAsync(
						session,
						output,
						keyReader,
						Math.Max(1, visibleRows),
						ansiEnabled,
						fetchNextPayload,
						cancellationToken)
					.ConfigureAwait(false);
				break;
		}
	}

	private static ReplPagerMode ResolveMode(ReplPagerMode pagerMode, bool ansiEnabled) =>
		pagerMode == ReplPagerMode.Auto
			? ansiEnabled ? ReplPagerMode.Full : ReplPagerMode.More
			: pagerMode;

	private static async ValueTask<bool> TryRenderCustomAsync(
		ReplPagerMode mode,
		IEnumerable<IReplPagerRenderer>? pagerRenderers,
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		if (pagerRenderers is null)
		{
			return false;
		}

		foreach (var renderer in pagerRenderers)
		{
			if (renderer.Mode != mode)
			{
				continue;
			}

			await renderer.RenderAsync(
					new ReplPagerRenderContext(
						payload,
						output,
						keyReader,
						visibleRows,
						visibleRowsProvider,
						ansiEnabled,
						hasMorePayload,
						fetchNextPayload is null ? null : FetchPublicPayloadAsync),
					cancellationToken)
				.ConfigureAwait(false);
			return true;
		}

		return false;

		async ValueTask<ReplPagerPayload?> FetchPublicPayloadAsync(CancellationToken token)
		{
			var next = await fetchNextPayload!(token).ConfigureAwait(false);
			return next is null ? null : new ReplPagerPayload(next.Payload, next.HasMore);
		}
	}

	private static async ValueTask RenderMoreAsync(
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		bool useTransientPrompt,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		session.PageSize = Math.Max(1, visibleRows);
		session.NextWindow = session.PageSize;
		var headerWritten = false;
		while (true)
		{
			if (session.Lines.Count == 0
				&& !await TryFetchIntoSessionAsync(session, fetchNextPayload, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			if (!headerWritten)
			{
				foreach (var headerLine in session.HeaderLines)
				{
					await output.WriteLineAsync(headerLine).ConfigureAwait(false);
				}

				headerWritten = true;
			}

			while (session.Index < session.Lines.Count)
			{
				await WriteMoreWindowAsync(session, output, fetchNextPayload, cancellationToken).ConfigureAwait(false);
				if (session.Index >= session.Lines.Count)
				{
					break;
				}

				if (await ReadMoreActionAsync(session, output, keyReader, useTransientPrompt, cancellationToken).ConfigureAwait(false) == PagerAction.Quit)
				{
					return;
				}
			}

			if (!session.HasMorePayload || fetchNextPayload is null)
			{
				return;
			}

			if (await ReadMoreActionAsync(session, output, keyReader, useTransientPrompt, cancellationToken).ConfigureAwait(false) == PagerAction.Quit)
			{
				return;
			}

			if (!await TryFetchIntoSessionAsync(session, fetchNextPayload, cancellationToken).ConfigureAwait(false))
			{
				return;
			}
		}
	}

	private static async ValueTask WriteMoreWindowAsync(
		PagerSession session,
		TextWriter output,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		var written = 0;
		while (written < session.NextWindow)
		{
			if (session.Index >= session.Lines.Count)
			{
				if (!await TryFetchIntoSessionAsync(session, fetchNextPayload, cancellationToken).ConfigureAwait(false))
				{
					break;
				}
			}

			await output.WriteLineAsync(session.Lines[session.Index]).ConfigureAwait(false);
			session.Index++;
			written++;
		}
	}

	private static async ValueTask<bool> TryFetchIntoSessionAsync(
		PagerSession session,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		if (!session.HasMorePayload || fetchNextPayload is null)
		{
			return false;
		}

		var nextPayload = await fetchNextPayload(cancellationToken).ConfigureAwait(false);
		if (nextPayload is null)
		{
			session.HasMorePayload = false;
			return false;
		}

		session.Append(nextPayload.Payload, nextPayload.HasMore, nextPayload.ContainsPresentationChrome);
		return true;
	}

	private static async ValueTask<PagerAction> ReadMoreActionAsync(
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
		bool useTransientPrompt,
		CancellationToken cancellationToken)
	{
		await output.WriteAsync(MorePrompt).ConfigureAwait(false);
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
		while (true)
		{
			var key = await keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);

			switch (key.Key)
			{
				case ConsoleKey.Q:
				case ConsoleKey.Escape:
					await FinishMorePromptAsync(output, useTransientPrompt).ConfigureAwait(false);
					return PagerAction.Quit;
				case ConsoleKey.Enter:
				case ConsoleKey.DownArrow:
					await FinishMorePromptAsync(output, useTransientPrompt).ConfigureAwait(false);
					session.NextWindow = 1;
					return PagerAction.LineDown;
				case ConsoleKey.UpArrow:
				case ConsoleKey.PageUp:
				case ConsoleKey.Home:
				case ConsoleKey.End:
					continue;
				default:
					await FinishMorePromptAsync(output, useTransientPrompt).ConfigureAwait(false);
					session.NextWindow = session.PageSize;
					return PagerAction.PageDown;
			}
		}
	}

	private static async ValueTask FinishMorePromptAsync(TextWriter output, bool useTransientPrompt)
	{
		if (!useTransientPrompt)
		{
			await output.WriteLineAsync().ConfigureAwait(false);
			return;
		}

		await output.WriteAsync('\r').ConfigureAwait(false);
		await output.WriteAsync(MorePromptClear).ConfigureAwait(false);
		await output.WriteAsync('\r').ConfigureAwait(false);
	}

	private static async ValueTask RenderViewportAsync(
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		TerminalSurfaceMode surfaceMode,
		CancellationToken cancellationToken)
	{
		var state = new ViewportState(session, visibleRows);
		if (state.Session.Lines.Count == 0 && !state.Session.HasMorePayload)
		{
			return;
		}

		var surface = await TerminalSurfaceHost.EnterAsync(
				output,
				surfaceMode,
				cancellationToken)
			.ConfigureAwait(false);
		try
		{
			await EnsureViewportContentAsync(state.Session, fetchNextPayload, cancellationToken).ConfigureAwait(false);
			while (true)
			{
				var currentRows = GetCurrentVisibleRows(visibleRows, visibleRowsProvider);
				if (state.UpdateVisibleRows(currentRows))
				{
					await ClearViewportAsync(surface, state).ConfigureAwait(false);
				}

				await RenderViewportFrameAsync(state, surface, cancellationToken).ConfigureAwait(false);
				var key = await keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
				var beforeTopLine = state.TopLine;
				var action = ApplyViewportKey(state, key);
				if (action == PagerAction.Quit)
				{
					return;
				}

				if (ShouldFetchForViewportKey(state, action, beforeTopLine)
					&& state.Session.HasMorePayload
					&& fetchNextPayload is not null)
				{
					await FetchIntoSessionAsync(state.Session, fetchNextPayload, cancellationToken).ConfigureAwait(false);
					state.TopLine = Math.Min(beforeTopLine + GetViewportDelta(action, state.ViewportHeight), state.MaxTopLine);
				}
			}
		}
		finally
		{
			await surface.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static async ValueTask EnsureViewportContentAsync(
		PagerSession session,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken)
	{
		while (session.Lines.Count == 0 && session.HasMorePayload && fetchNextPayload is not null)
		{
			await FetchIntoSessionAsync(session, fetchNextPayload, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async ValueTask FetchIntoSessionAsync(
		PagerSession session,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>> fetchNextPayload,
		CancellationToken cancellationToken)
	{
		var nextPayload = await fetchNextPayload(cancellationToken).ConfigureAwait(false);
		if (nextPayload is null)
		{
			session.HasMorePayload = false;
			return;
		}

		session.Append(nextPayload.Payload, nextPayload.HasMore, nextPayload.ContainsPresentationChrome);
	}

	private static async ValueTask ClearViewportAsync(TerminalSurfaceScope surface, ViewportState state)
	{
		if (surface.Mode == TerminalSurfaceMode.AlternateScreen)
		{
			await surface.MoveHomeAsync().ConfigureAwait(false);
		}
		else if (state.RenderedHeight > 0)
		{
			await surface.MoveCursorUpAsync(state.RenderedHeight).ConfigureAwait(false);
			await surface.MoveToColumnStartAsync().ConfigureAwait(false);
		}

		await surface.ClearToEndOfScreenAsync().ConfigureAwait(false);
		state.ResetRenderedLineLengths();
	}

	private static async ValueTask RenderViewportFrameAsync(
		ViewportState state,
		TerminalSurfaceScope surface,
		CancellationToken cancellationToken)
	{
		await PositionViewportAsync(surface, state).ConfigureAwait(false);
		var row = 0;
		foreach (var headerLine in state.Session.HeaderLines)
		{
			await WriteViewportLineAsync(state, surface.Output, row++, headerLine).ConfigureAwait(false);
		}

		var take = Math.Min(state.ViewportHeight, Math.Max(0, state.Session.Lines.Count - state.TopLine));
		for (var i = 0; i < take; i++)
		{
			await WriteViewportLineAsync(state, surface.Output, row++, state.Session.Lines[state.TopLine + i]).ConfigureAwait(false);
		}

		for (var i = take; i < state.ViewportHeight; i++)
		{
			await WriteViewportLineAsync(state, surface.Output, row++, string.Empty).ConfigureAwait(false);
		}

		var lastLine = state.Session.Lines.Count == 0
			? 0
			: Math.Min(state.Session.Lines.Count, state.TopLine + state.ViewportHeight);
		var status = CreateViewportStatus(state, lastLine);
		await WriteViewportLineAsync(state, surface.Output, row++, status, appendNewLine: false).ConfigureAwait(false);
		state.RenderedHeight = row;
		await surface.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string CreateViewportStatus(ViewportState state, int lastLine)
	{
		if (state.Session.Lines.Count == 0)
		{
			return "-- result-flow: loading --";
		}

		return state.Session.BufferLimitReached
			? string.Format(
				System.Globalization.CultureInfo.InvariantCulture,
				FullStatusBufferLimitFormat,
				state.TopLine + 1,
				lastLine,
				state.Session.Lines.Count)
			: string.Format(
				System.Globalization.CultureInfo.InvariantCulture,
				FullStatusFormat,
				state.TopLine + 1,
				lastLine,
				state.Session.Lines.Count,
				state.Session.HasMorePayload ? "+" : string.Empty);
	}

	private static async ValueTask PositionViewportAsync(TerminalSurfaceScope surface, ViewportState state)
	{
		if (surface.Mode == TerminalSurfaceMode.AlternateScreen)
		{
			await surface.MoveHomeAsync().ConfigureAwait(false);
		}
		else if (state.RenderedHeight > 0)
		{
			await surface.MoveCursorUpAsync(state.RenderedHeight).ConfigureAwait(false);
			await surface.MoveToColumnStartAsync().ConfigureAwait(false);
		}
	}

	private static async ValueTask WriteViewportLineAsync(
		ViewportState state,
		TextWriter output,
		int row,
		string line,
		bool appendNewLine = true)
	{
		await output.WriteAsync(line).ConfigureAwait(false);
		var previousLength = state.GetRenderedLineLength(row);
		if (previousLength > line.Length)
		{
			await WriteSpacesAsync(output, previousLength - line.Length).ConfigureAwait(false);
		}

		state.SetRenderedLineLength(row, line.Length);
		if (appendNewLine)
		{
			await output.WriteLineAsync().ConfigureAwait(false);
		}
	}

	private static PagerAction ApplyViewportKey(ViewportState state, ConsoleKeyInfo key)
	{
		switch (key.Key)
		{
			case ConsoleKey.Q:
			case ConsoleKey.Escape:
				return PagerAction.Quit;
			case ConsoleKey.Spacebar:
			case ConsoleKey.PageDown:
			case ConsoleKey.F:
				state.TopLine = Math.Min(state.TopLine + state.ViewportHeight, state.MaxTopLine);
				return PagerAction.PageDown;
			case ConsoleKey.Enter:
			case ConsoleKey.DownArrow:
			case ConsoleKey.J:
				state.TopLine = Math.Min(state.TopLine + 1, state.MaxTopLine);
				return PagerAction.LineDown;
			case ConsoleKey.UpArrow:
			case ConsoleKey.K:
				state.TopLine = Math.Max(0, state.TopLine - 1);
				return PagerAction.LineUp;
			case ConsoleKey.PageUp:
			case ConsoleKey.B:
				state.TopLine = Math.Max(0, state.TopLine - state.ViewportHeight);
				return PagerAction.PageUp;
			case ConsoleKey.Home:
			case ConsoleKey.G when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
				state.TopLine = 0;
				return PagerAction.Home;
			case ConsoleKey.End:
				state.TopLine = state.MaxTopLine;
				return PagerAction.End;
			default:
				return PagerAction.None;
		}
	}

	private static bool ShouldFetchForViewportKey(ViewportState state, PagerAction action, int beforeTopLine) =>
		action switch
		{
			PagerAction.PageDown => state.HasReachedBottom && state.Session.Lines.Count > state.ViewportHeight,
			PagerAction.LineDown => beforeTopLine == state.TopLine && state.HasReachedBottom,
			_ => false,
		};

	private static int GetViewportDelta(PagerAction action, int viewportHeight) =>
		action == PagerAction.PageDown ? viewportHeight : 1;

	private static async ValueTask WriteSpacesAsync(TextWriter output, int count)
	{
		if (count <= 0)
		{
			return;
		}

		while (count > 0)
		{
			var take = Math.Min(count, SpacePadding.Length);
			await output.WriteAsync(SpacePadding.AsMemory(0, take)).ConfigureAwait(false);
			count -= take;
		}
	}

	private static int GetCurrentVisibleRows(int fallbackVisibleRows, Func<int>? visibleRowsProvider)
	{
		if (visibleRowsProvider is null)
		{
			return Math.Max(2, fallbackVisibleRows);
		}

		try
		{
			return Math.Max(2, visibleRowsProvider());
		}
		catch (Exception ex) when (ex is IOException
			or PlatformNotSupportedException
			or InvalidOperationException
			or System.Security.SecurityException)
		{
			return Math.Max(2, fallbackVisibleRows);
		}
	}

	private enum PagerAction
	{
		None,
		LineDown,
		LineUp,
		PageDown,
		PageUp,
		Home,
		End,
		Quit,
	}

	private sealed class ViewportState
	{
		private readonly List<int> _renderedLineLengths = [];

		public ViewportState(PagerSession session, int visibleRows)
		{
			Session = session;
			Session.PageSize = Math.Max(1, CalculateViewportHeight(visibleRows));
			Session.NextWindow = Session.PageSize;
			VisibleRows = Math.Max(2, visibleRows);
			ViewportHeight = CalculateViewportHeight(VisibleRows);
		}

		public PagerSession Session { get; }

		public int VisibleRows { get; private set; }

		public int ViewportHeight { get; private set; }

		public int TopLine { get; set; }

		public int RenderedHeight { get; set; }

		public int MaxTopLine => Math.Max(0, Session.Lines.Count - ViewportHeight);

		public bool HasReachedBottom => TopLine >= MaxTopLine;

		public bool UpdateVisibleRows(int visibleRows)
		{
			visibleRows = Math.Max(2, visibleRows);
			if (VisibleRows == visibleRows)
			{
				return false;
			}

			VisibleRows = visibleRows;
			ViewportHeight = CalculateViewportHeight(visibleRows);
			TopLine = Math.Min(TopLine, MaxTopLine);
			Session.PageSize = ViewportHeight;
			return true;
		}

		public int GetRenderedLineLength(int row) =>
			row < _renderedLineLengths.Count ? _renderedLineLengths[row] : 0;

		public void SetRenderedLineLength(int row, int length)
		{
			while (_renderedLineLengths.Count <= row)
			{
				_renderedLineLengths.Add(0);
			}

			_renderedLineLengths[row] = length;
		}

		public void ResetRenderedLineLengths() => _renderedLineLengths.Clear();

		private int CalculateViewportHeight(int visibleRows) =>
			Math.Max(1, visibleRows - Session.HeaderLines.Count - 1);
	}

}
