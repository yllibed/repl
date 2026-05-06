namespace Repl;

internal static class ResultFlowPager
{
	private const string MorePrompt = "--More-- Space/PageDown: continue, Enter/Down: line, Up/PageUp: ignored, q/Esc: stop";
	private const string FullStatus = "-- result-flow {0}-{1}/{2}{3}  Space: next  Up/Down: scroll  Home/End: known bounds  q: quit --";
	private const string EnterAlternateScreen = "\u001b[?1049h";
	private const string LeaveAlternateScreen = "\u001b[?1049l";
	private const string HideCursor = "\u001b[?25l";
	private const string ShowCursor = "\u001b[?25h";
	private const string CursorHome = "\u001b[H";
	private const string ClearToEndOfScreen = "\u001b[J";
	private const string DisableLineWrap = "\u001b[?7l";
	private const string EnableLineWrap = "\u001b[?7h";
	private static readonly System.Text.CompositeFormat FullStatusFormat =
		System.Text.CompositeFormat.Parse(FullStatus);

	public static int CountLines(string payload) => PagerPayloadParser.Parse(payload, header: null).TotalLineCount;

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
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);

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

		var session = new PagerSession(payload, hasMorePayload);
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
						useAlternateScreen: true,
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
						useAlternateScreen: false,
						cancellationToken)
					.ConfigureAwait(false);
				break;
			default:
				await RenderMoreAsync(
						session,
						output,
						keyReader,
						Math.Max(1, visibleRows),
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
				await WriteMoreWindowAsync(session, output).ConfigureAwait(false);
				if (session.Index >= session.Lines.Count)
				{
					break;
				}

				if (await ReadMoreActionAsync(session, output, keyReader, cancellationToken).ConfigureAwait(false) == PagerAction.Quit)
				{
					return;
				}
			}

			if (!session.HasMorePayload || fetchNextPayload is null)
			{
				return;
			}

			if (await ReadMoreActionAsync(session, output, keyReader, cancellationToken).ConfigureAwait(false) == PagerAction.Quit)
			{
				return;
			}

			if (!await TryFetchIntoSessionAsync(session, fetchNextPayload, cancellationToken).ConfigureAwait(false))
			{
				return;
			}
		}
	}

	private static async ValueTask WriteMoreWindowAsync(PagerSession session, TextWriter output)
	{
		var take = Math.Min(session.NextWindow, session.Lines.Count - session.Index);
		for (var i = 0; i < take; i++)
		{
			await output.WriteLineAsync(session.Lines[session.Index + i]).ConfigureAwait(false);
		}

		session.Index += take;
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

		session.Append(nextPayload.Payload, nextPayload.HasMore);
		return true;
	}

	private static async ValueTask<PagerAction> ReadMoreActionAsync(
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
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
					await output.WriteLineAsync().ConfigureAwait(false);
					return PagerAction.Quit;
				case ConsoleKey.Enter:
				case ConsoleKey.DownArrow:
					await output.WriteLineAsync().ConfigureAwait(false);
					session.NextWindow = 1;
					return PagerAction.LineDown;
				case ConsoleKey.UpArrow:
				case ConsoleKey.PageUp:
				case ConsoleKey.Home:
				case ConsoleKey.End:
					continue;
				default:
					await output.WriteLineAsync().ConfigureAwait(false);
					session.NextWindow = session.PageSize;
					return PagerAction.PageDown;
			}
		}
	}

	private static async ValueTask RenderViewportAsync(
		PagerSession session,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		bool useAlternateScreen,
		CancellationToken cancellationToken)
	{
		var state = new ViewportState(session, visibleRows);
		if (state.Session.Lines.Count == 0 && !state.Session.HasMorePayload)
		{
			return;
		}

		if (useAlternateScreen)
		{
			await output.WriteAsync(EnterAlternateScreen).ConfigureAwait(false);
		}

		await output.WriteAsync(HideCursor).ConfigureAwait(false);
		await output.WriteAsync(DisableLineWrap).ConfigureAwait(false);
		await output.WriteAsync(CursorHome).ConfigureAwait(false);
		await output.WriteAsync(ClearToEndOfScreen).ConfigureAwait(false);

		try
		{
			await EnsureViewportContentAsync(state.Session, fetchNextPayload, cancellationToken).ConfigureAwait(false);
			while (true)
			{
				var currentRows = GetCurrentVisibleRows(visibleRows, visibleRowsProvider);
				if (state.UpdateVisibleRows(currentRows))
				{
					await ClearViewportAsync(output, state, useAlternateScreen).ConfigureAwait(false);
				}

				await RenderViewportFrameAsync(state, output, useAlternateScreen, cancellationToken).ConfigureAwait(false);
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
			await output.WriteAsync(EnableLineWrap).ConfigureAwait(false);
			await output.WriteAsync(ShowCursor).ConfigureAwait(false);
			if (useAlternateScreen)
			{
				await output.WriteAsync(LeaveAlternateScreen).ConfigureAwait(false);
			}

			await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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

		session.Append(nextPayload.Payload, nextPayload.HasMore);
	}

	private static async ValueTask ClearViewportAsync(TextWriter output, ViewportState state, bool useAlternateScreen)
	{
		if (useAlternateScreen)
		{
			await output.WriteAsync(CursorHome).ConfigureAwait(false);
		}
		else if (state.RenderedHeight > 0)
		{
			await output.WriteAsync($"\u001b[{state.RenderedHeight}A").ConfigureAwait(false);
			await output.WriteAsync('\r').ConfigureAwait(false);
		}

		await output.WriteAsync(ClearToEndOfScreen).ConfigureAwait(false);
		state.ResetRenderedLineLengths();
	}

	private static async ValueTask RenderViewportFrameAsync(
		ViewportState state,
		TextWriter output,
		bool useAlternateScreen,
		CancellationToken cancellationToken)
	{
		await PositionViewportAsync(output, state, useAlternateScreen).ConfigureAwait(false);
		var row = 0;
		foreach (var headerLine in state.Session.HeaderLines)
		{
			await WriteViewportLineAsync(state, output, row++, headerLine).ConfigureAwait(false);
		}

		var take = Math.Min(state.ViewportHeight, Math.Max(0, state.Session.Lines.Count - state.TopLine));
		for (var i = 0; i < take; i++)
		{
			await WriteViewportLineAsync(state, output, row++, state.Session.Lines[state.TopLine + i]).ConfigureAwait(false);
		}

		for (var i = take; i < state.ViewportHeight; i++)
		{
			await WriteViewportLineAsync(state, output, row++, string.Empty).ConfigureAwait(false);
		}

		var lastLine = state.Session.Lines.Count == 0
			? 0
			: Math.Min(state.Session.Lines.Count, state.TopLine + state.ViewportHeight);
		var status = state.Session.Lines.Count == 0
			? "-- result-flow: loading --"
			: string.Format(
				System.Globalization.CultureInfo.InvariantCulture,
				FullStatusFormat,
				state.TopLine + 1,
				lastLine,
				state.Session.Lines.Count,
				state.Session.HasMorePayload ? "+" : string.Empty);
		await WriteViewportLineAsync(state, output, row++, status, appendNewLine: false).ConfigureAwait(false);
		state.RenderedHeight = row;
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask PositionViewportAsync(TextWriter output, ViewportState state, bool useAlternateScreen)
	{
		if (useAlternateScreen)
		{
			await output.WriteAsync(CursorHome).ConfigureAwait(false);
		}
		else if (state.RenderedHeight > 0)
		{
			await output.WriteAsync($"\u001b[{state.RenderedHeight}A").ConfigureAwait(false);
			await output.WriteAsync('\r').ConfigureAwait(false);
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
			await output.WriteAsync(new string(' ', previousLength - line.Length)).ConfigureAwait(false);
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
		catch (IOException)
		{
			return Math.Max(2, fallbackVisibleRows);
		}
		catch (PlatformNotSupportedException)
		{
			return Math.Max(2, fallbackVisibleRows);
		}
		catch (InvalidOperationException)
		{
			return Math.Max(2, fallbackVisibleRows);
		}
		catch (System.Security.SecurityException)
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

	private sealed class PagerSession
	{
		private readonly PagerHeader _header;

		public PagerSession(string initialPayload, bool hasMorePayload)
		{
			var parsed = PagerPayloadParser.Parse(initialPayload, header: null);
			_header = parsed.Header;
			Lines = [.. parsed.ContentLines];
			HasMorePayload = hasMorePayload;
			PageSize = 1;
			NextWindow = 1;
		}

		public IReadOnlyList<string> HeaderLines => _header.Lines;

		public List<string> Lines { get; }

		public int PageSize { get; set; }

		public int NextWindow { get; set; }

		public int Index { get; set; }

		public bool HasMorePayload { get; set; }

		public void Append(string payload, bool hasMorePayload)
		{
			var parsed = PagerPayloadParser.Parse(payload, _header);
			Lines.AddRange(parsed.ContentLines);
			HasMorePayload = hasMorePayload;
		}
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

	private sealed record PagerHeader(IReadOnlyList<string> Lines, IReadOnlySet<string> NormalizedLines)
	{
		public static PagerHeader Empty { get; } = new([], new HashSet<string>(StringComparer.Ordinal));
	}

	private sealed record ParsedPagerPayload(PagerHeader Header, IReadOnlyList<string> ContentLines)
	{
		public int TotalLineCount => Header.Lines.Count + ContentLines.Count;
	}

	private static class PagerPayloadParser
	{
		public static ParsedPagerPayload Parse(string payload, PagerHeader? header)
		{
			var lines = SplitLines(payload);
			var resolvedHeader = header ?? DetectHeader(lines);
			var content = new List<string>();
			var headerLineCount = header is null ? resolvedHeader.Lines.Count : 0;
			for (var i = headerLineCount; i < lines.Length; i++)
			{
				var normalized = NormalizeLine(lines[i]);
				if (resolvedHeader.NormalizedLines.Contains(normalized) || IsPageFooterLine(lines[i]))
				{
					continue;
				}

				content.Add(lines[i]);
			}

			return new ParsedPagerPayload(resolvedHeader, content);
		}

		private static PagerHeader DetectHeader(string[] lines)
		{
			if (lines.Length == 0)
			{
				return PagerHeader.Empty;
			}

			if (lines.Length > 1 && IsPlainTableSeparator(lines[1]))
			{
				return CreateHeader(lines.Take(2).ToArray());
			}

			if (IsPlainHumanTableHeader(lines[0]))
			{
				return CreateHeader([lines[0]]);
			}

			return lines[0].Contains("\u001b[1m", StringComparison.Ordinal)
				? CreateHeader([lines[0]])
				: PagerHeader.Empty;
		}

		private static PagerHeader CreateHeader(string[] lines) =>
			new(
				lines,
				lines.Select(NormalizeLine).ToHashSet(StringComparer.Ordinal));

		private static bool IsPlainTableSeparator(string line)
		{
			var text = line.Trim();
			return text.Length > 0
				&& text.All(ch => ch is '-' or ' ' or '\t')
				&& text.Contains('-', StringComparison.Ordinal);
		}

		private static bool IsPlainHumanTableHeader(string line)
		{
			var text = line.TrimStart();
			return text.StartsWith("# ", StringComparison.Ordinal)
				&& text.Contains("  ", StringComparison.Ordinal);
		}

		private static bool IsPageFooterLine(string line) =>
			line.StartsWith("Showing ", StringComparison.Ordinal)
			&& (line.Contains(" of ", StringComparison.Ordinal)
				|| line.Contains(" result(s).", StringComparison.Ordinal))
			&& (line.EndsWith('.')
				|| line.Contains("Next data page: rerun with --result:cursor ", StringComparison.Ordinal));

		private static string[] SplitLines(string payload)
		{
			if (string.IsNullOrEmpty(payload))
			{
				return [];
			}

			var lines = new List<string>();
			foreach (var line in payload.AsSpan().EnumerateLines())
			{
				lines.Add(line.ToString());
			}

			if (lines.Count > 0 && lines[^1].Length == 0)
			{
				lines.RemoveAt(lines.Count - 1);
			}

			return [.. lines];
		}

		private static string NormalizeLine(string line)
		{
			if (!line.Contains('\u001b', StringComparison.Ordinal))
			{
				return line.Trim();
			}

			var builder = new System.Text.StringBuilder(line.Length);
			for (var i = 0; i < line.Length; i++)
			{
				if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
				{
					i += 2;
					while (i < line.Length && (line[i] < '@' || line[i] > '~'))
					{
						i++;
					}

					continue;
				}

				builder.Append(line[i]);
			}

			return builder.ToString().Trim();
		}
	}
}
