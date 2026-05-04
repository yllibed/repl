namespace Repl;

internal static class ResultFlowPager
{
	private const string MorePrompt = "--More--";

	public static int CountLines(string payload) => SplitLines(payload).Length;

	public static async ValueTask WriteAsync(
		string payload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);

		var lines = SplitLines(payload);
		if (lines.Length == 0)
		{
			return;
		}

		var pageSize = Math.Max(1, visibleRows);
		var nextWindow = pageSize;
		var index = 0;
		while (index < lines.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var take = Math.Min(nextWindow, lines.Length - index);
			for (var i = 0; i < take; i++)
			{
				await output.WriteLineAsync(lines[index + i]).ConfigureAwait(false);
			}

			index += take;
			if (index >= lines.Length)
			{
				break;
			}

			await output.WriteAsync(MorePrompt).ConfigureAwait(false);
			await output.FlushAsync(cancellationToken).ConfigureAwait(false);
			var key = await keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
			await output.WriteLineAsync().ConfigureAwait(false);

			switch (key.Key)
			{
				case ConsoleKey.Q:
				case ConsoleKey.Escape:
					return;
				case ConsoleKey.Enter:
				case ConsoleKey.DownArrow:
					nextWindow = 1;
					break;
				case ConsoleKey.UpArrow:
				case ConsoleKey.PageUp:
					index = Math.Max(0, index - pageSize - take);
					nextWindow = key.Key == ConsoleKey.UpArrow ? 1 : pageSize;
					break;
				default:
					nextWindow = pageSize;
					break;
			}
		}
	}

	private static string[] SplitLines(string payload) =>
		payload
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\r', '\n')
			.Split('\n');
}
