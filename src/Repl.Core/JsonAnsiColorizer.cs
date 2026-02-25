using System.Text;
using System.Text.Json;

namespace Repl;

internal static class JsonAnsiColorizer
{
	public static string Colorize(string json, AnsiPalette palette)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return json;
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			var builder = new StringBuilder(json.Length + 64);
			RenderElement(doc.RootElement, builder, indentLevel: 0, palette);
			return builder.ToString();
		}
		catch
		{
			return json;
		}
	}

	private static void RenderElement(
		JsonElement element,
		StringBuilder builder,
		int indentLevel,
		AnsiPalette palette)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				RenderObject(element, builder, indentLevel, palette);
				return;
			case JsonValueKind.Array:
				RenderArray(element, builder, indentLevel, palette);
				return;
			case JsonValueKind.String:
				builder.Append(Style(QuoteJsonString(element.GetString()), palette.JsonStringStyle));
				return;
			case JsonValueKind.Number:
				builder.Append(Style(element.GetRawText(), palette.JsonNumberStyle));
				return;
			case JsonValueKind.True:
			case JsonValueKind.False:
			case JsonValueKind.Null:
				builder.Append(Style(element.GetRawText(), palette.JsonKeywordStyle));
				return;
			default:
				builder.Append(element.GetRawText());
				return;
		}
	}

	private static void RenderObject(
		JsonElement element,
		StringBuilder builder,
		int indentLevel,
		AnsiPalette palette)
	{
		var properties = element.EnumerateObject().ToArray();
		if (properties.Length == 0)
		{
			builder.Append(Style("{}", palette.JsonPunctuationStyle));
			return;
		}

		builder.Append(Style("{", palette.JsonPunctuationStyle)).AppendLine();
		for (var i = 0; i < properties.Length; i++)
		{
			AppendIndent(builder, indentLevel + 1);
			var property = properties[i];
			builder.Append(Style(QuoteJsonString(property.Name), palette.JsonPropertyStyle));
			builder.Append(Style(": ", palette.JsonPunctuationStyle));
			RenderElement(property.Value, builder, indentLevel + 1, palette);
			if (i < properties.Length - 1)
			{
				builder.Append(Style(",", palette.JsonPunctuationStyle));
			}

			builder.AppendLine();
		}

		AppendIndent(builder, indentLevel);
		builder.Append(Style("}", palette.JsonPunctuationStyle));
	}

	private static void RenderArray(
		JsonElement element,
		StringBuilder builder,
		int indentLevel,
		AnsiPalette palette)
	{
		var items = element.EnumerateArray().ToArray();
		if (items.Length == 0)
		{
			builder.Append(Style("[]", palette.JsonPunctuationStyle));
			return;
		}

		builder.Append(Style("[", palette.JsonPunctuationStyle)).AppendLine();
		for (var i = 0; i < items.Length; i++)
		{
			AppendIndent(builder, indentLevel + 1);
			RenderElement(items[i], builder, indentLevel + 1, palette);
			if (i < items.Length - 1)
			{
				builder.Append(Style(",", palette.JsonPunctuationStyle));
			}

			builder.AppendLine();
		}

		AppendIndent(builder, indentLevel);
		builder.Append(Style("]", palette.JsonPunctuationStyle));
	}

	private static string Style(string value, string style) =>
		string.IsNullOrEmpty(style) ? value : AnsiText.Apply(value, style);

	private static string QuoteJsonString(string? value)
	{
		if (value is null)
		{
			return "null";
		}

		var builder = new StringBuilder(value.Length + 4);
		builder.Append('"');
		foreach (var ch in value)
		{
			switch (ch)
			{
				case '"':
					builder.Append("\\\"");
					break;
				case '\\':
					builder.Append("\\\\");
					break;
				case '\b':
					builder.Append("\\b");
					break;
				case '\f':
					builder.Append("\\f");
					break;
				case '\n':
					builder.Append("\\n");
					break;
				case '\r':
					builder.Append("\\r");
					break;
				case '\t':
					builder.Append("\\t");
					break;
				default:
					if (char.IsControl(ch))
					{
						builder.Append("\\u").Append(((int)ch).ToString("x4"));
					}
					else
					{
						builder.Append(ch);
					}

					break;
			}
		}

		builder.Append('"');
		return builder.ToString();
	}

	private static void AppendIndent(StringBuilder builder, int indentLevel) =>
		builder.Append(' ', indentLevel * 2);
}
