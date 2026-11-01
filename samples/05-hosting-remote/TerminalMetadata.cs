using Repl;

namespace HostingRemoteSample;

internal sealed record TerminalMetadata(
	string? Terminal,
	(int Width, int Height)? WindowSize,
	bool? AnsiSupported,
	TerminalCapabilities? Capabilities)
{
	internal static TerminalMetadata FromQuery(IQueryCollection query)
	{
		var terminal = query["terminal"].ToString();
		var cols = ParsePositiveInt(query["cols"].ToString());
		var rows = ParsePositiveInt(query["rows"].ToString());
		return new TerminalMetadata(
			string.IsNullOrWhiteSpace(terminal) ? null : terminal,
			cols is > 0 && rows is > 0 ? (cols.Value, rows.Value) : null,
			bool.TryParse(query["ansi"].ToString(), out var ansi) ? ansi : null,
			ParseCapabilities(query["capabilities"].ToString()));
	}

	internal void ApplyTo(SessionTracker tracker, string sessionName)
	{
		if (Terminal is null && WindowSize is null && AnsiSupported is null && Capabilities is null)
		{
			return;
		}
		tracker.UpdateMetadataByName(
			sessionName,
			terminal: Terminal,
			screen: WindowSize is { } size ? $"{size.Width}x{size.Height}" : null,
			capabilities: Capabilities,
			ansiSupported: AnsiSupported);
	}

	internal static void ApplyControlMessage(SessionTracker tracker, string sessionName, TerminalControlMessage message) =>
		tracker.UpdateMetadataByName(
			sessionName,
			terminal: message.TerminalIdentity,
			screen: message.WindowSize is { } size ? $"{size.Width}x{size.Height}" : null,
			capabilities: message.TerminalCapabilities,
			ansiSupported: message.AnsiSupported);

	private static int? ParsePositiveInt(string raw) =>
		int.TryParse(raw, out var value) && value > 0 ? value : null;

	private static TerminalCapabilities? ParseCapabilities(string raw)
	{
		var parsed = TerminalCapabilities.None;
		foreach (var token in raw.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (Enum.TryParse<TerminalCapabilities>(token, ignoreCase: true, out var capability))
			{
				parsed |= capability;
			}
		}
		return parsed == TerminalCapabilities.None ? null : parsed;
	}
}
