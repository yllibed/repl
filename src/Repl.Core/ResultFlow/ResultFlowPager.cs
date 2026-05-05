namespace Repl;

internal static class ResultFlowPager
{
	private const string MorePrompt = "--More-- Space/PageDown: continue, Enter/Down: line, Up/PageUp: back, q/Esc: stop";

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
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? fetchNextPayload,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);

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

	private static string[] SplitLines(string payload) =>
		string.IsNullOrEmpty(payload)
			? []
			: payload
				.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Replace('\r', '\n')
				.Split('\n');

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
}
