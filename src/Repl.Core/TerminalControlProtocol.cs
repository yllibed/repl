using System.Text.Json;

namespace Repl;

/// <summary>
/// Parses and formats lightweight terminal control messages transported over text channels.
/// </summary>
public static class TerminalControlProtocol
{
	/// <summary>
	/// Prefix reserved for control messages.
	/// </summary>
	public const string Prefix = "@@repl:";

	private const string HelloVerb = "hello";
	private const string ResizeVerb = "resize";

	/// <summary>
	/// Tries to parse a raw input payload into a structured terminal control message.
	/// </summary>
	public static bool TryParse(string? text, out TerminalControlMessage message)
	{
		message = default!;
		if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		var separatorIndex = text.IndexOf(' ');
		var verb = separatorIndex < 0
			? text[Prefix.Length..].Trim()
			: text[Prefix.Length..separatorIndex].Trim();
		var payload = separatorIndex < 0 ? string.Empty : text[(separatorIndex + 1)..].Trim();

		return verb switch
		{
			HelloVerb => TryParsePayload(payload, TerminalControlMessageKind.Hello, out message),
			ResizeVerb => TryParsePayload(payload, TerminalControlMessageKind.Resize, out message),
			_ => false,
		};
	}

	private static bool TryParsePayload(
		string payload,
		TerminalControlMessageKind kind,
		out TerminalControlMessage message)
	{
		message = new TerminalControlMessage(kind);
		if (string.IsNullOrWhiteSpace(payload))
		{
			return true;
		}

		try
		{
			using var document = JsonDocument.Parse(payload);
			var root = document.RootElement;

			string? terminalIdentity = null;
			(int Width, int Height)? windowSize = null;
			bool? ansiSupported = null;
			TerminalCapabilities? capabilities = null;

			if (root.TryGetProperty("terminal", out var terminalElement) && terminalElement.ValueKind == JsonValueKind.String)
			{
				terminalIdentity = terminalElement.GetString();
			}

			var cols = 0;
			var rows = 0;
			var hasCols = root.TryGetProperty("cols", out var colsElement) && colsElement.TryGetInt32(out cols);
			var hasRows = root.TryGetProperty("rows", out var rowsElement) && rowsElement.TryGetInt32(out rows);
			if (hasCols && hasRows && cols > 0 && rows > 0)
			{
				windowSize = (cols, rows);
			}

			if (root.TryGetProperty("ansi", out var ansiElement)
			    && ansiElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
			{
				ansiSupported = ansiElement.GetBoolean();
			}

			if (root.TryGetProperty("capabilities", out var capsElement))
			{
				if (capsElement.ValueKind == JsonValueKind.String)
				{
					capabilities = ParseCapabilities(capsElement.GetString());
				}
				else if (capsElement.ValueKind == JsonValueKind.Number && capsElement.TryGetInt32(out var raw))
				{
					capabilities = (TerminalCapabilities)raw;
				}
			}

			message = new TerminalControlMessage(kind, terminalIdentity, windowSize, ansiSupported, capabilities);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static TerminalCapabilities ParseCapabilities(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return TerminalCapabilities.None;
		}

		return Enum.TryParse<TerminalCapabilities>(value, ignoreCase: true, out var parsed)
			? parsed
			: TerminalCapabilities.None;
	}
}
