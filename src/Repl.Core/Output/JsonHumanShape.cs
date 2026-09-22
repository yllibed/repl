using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Repl;

/// <summary>
/// Recognizes JSON data — <see cref="JsonNode"/> and <see cref="JsonElement"/> — for the human renderers,
/// which would otherwise reflect over the CLR members of those types (<c>Options</c>, <c>Parent</c>,
/// <c>Root</c>, <c>Count</c>, <c>ValueKind</c>) instead of showing the data.
/// </summary>
/// <remarks>
/// Values render as compact JSON literals, so a string reads <c>"x"</c>, an explicit JSON null reads
/// <c>null</c>, and a nested object or array stays visible as itself. The relaxed encoder keeps non-ASCII
/// text readable and escapes every control character; format characters (bidirectional overrides and
/// isolates, zero-width marks), which it lets through, are escaped here too, so a JSON string can neither
/// drive the terminal it is printed to nor make it display something other than the data. Nothing here
/// mutates the value it reads.
/// </remarks>
internal static class JsonHumanShape
{
	private static readonly JsonWriterOptions LiteralWriterOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static bool IsJson([NotNullWhen(true)] object? value) => value is JsonNode or JsonElement;

	/// <summary>Whether items declared as <paramref name="type"/> are JSON data, nullable elements included.</summary>
	public static bool IsJsonType(Type type) =>
		typeof(JsonNode).IsAssignableFrom(type) || (Nullable.GetUnderlyingType(type) ?? type) == typeof(JsonElement);

	/// <summary>
	/// Reads <paramref name="value"/> as a JSON node. A <see cref="JsonElement"/> is wrapped, and the element
	/// itself is never modified. A JSON null comes back as a <see langword="null"/> node with
	/// <see langword="true"/>.
	/// </summary>
	public static bool TryGetNode(object? value, out JsonNode? node)
	{
		switch (value)
		{
			case JsonNode jsonNode:
				node = jsonNode;
				return true;
			case JsonElement element:
				node = element.ValueKind switch
				{
					JsonValueKind.Object => JsonObject.Create(element),
					JsonValueKind.Array => JsonArray.Create(element),
					JsonValueKind.Null or JsonValueKind.Undefined => null,
					_ => JsonValue.Create(element),
				};
				return true;
			default:
				node = null;
				return false;
		}
	}

	/// <summary>
	/// The literal for an item of a JSON collection: <see langword="null"/> — a JSON null — reads
	/// <c>null</c>. <see langword="false"/> for a value that is not JSON at all.
	/// </summary>
	public static bool TryLiteral(object? value, [NotNullWhen(true)] out string? literal)
	{
		if (value is null)
		{
			literal = Literal(node: null);
			return true;
		}

		if (TryGetNode(value, out var node))
		{
			literal = Literal(node);
			return true;
		}

		literal = null;
		return false;
	}

	/// <summary>
	/// The compact JSON text of <paramref name="node"/>. Written with no serializer options, so a
	/// <see cref="JsonValue"/> wrapping a CLR object keeps the type information it was created with;
	/// options without a type resolver make that write throw.
	/// </summary>
	public static string Literal(JsonNode? node)
	{
		if (node is null)
		{
			return "null";
		}

		var buffer = new ArrayBufferWriter<byte>();
		// Synchronous on purpose: the writer targets an in-memory buffer, so disposing it only flushes there.
#pragma warning disable MA0045
		using (var writer = new Utf8JsonWriter(buffer, LiteralWriterOptions))
#pragma warning restore MA0045
		{
			node.WriteTo(writer, options: null);
		}

		return EscapeFormatCharacters(Encoding.UTF8.GetString(buffer.WrittenSpan));
	}

	/// <summary>A property name as a label: escaped like a JSON string, without the quotes.</summary>
	public static string Label(string key) =>
		EscapeFormatCharacters(JsonEncodedText.Encode(key, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());

	/// <summary>
	/// Reads <paramref name="values"/> as rows of JSON objects. The columns are the union of their keys in
	/// first-seen order, so a key missing from one row leaves its cell empty rather than dropping the row.
	/// A JSON null, as a <see langword="null"/> value or a null <see cref="JsonElement"/>, is an empty row. Fails if any other value is not a JSON object, or if no
	/// row has a key: a table with no columns would show nothing of them.
	/// </summary>
	public static bool TryGetObjectRows(
		IReadOnlyList<object?> values,
		[NotNullWhen(true)] out string[]? columns,
		[NotNullWhen(true)] out JsonObject?[]? rows)
	{
		columns = null;
		rows = null;
		var converted = new JsonObject?[values.Count];
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var ordered = new List<string>();
		for (var i = 0; i < values.Count; i++)
		{
			if (values[i] is null)
			{
				continue;
			}

			if (!TryGetNode(values[i], out var node))
			{
				return false;
			}

			// A JsonElement of kind Null (or Undefined, a default element) is no data either: an empty row, like
			// a CLR null.
			if (node is null)
			{
				continue;
			}

			if (node is not JsonObject row)
			{
				return false;
			}

			converted[i] = row;
			foreach (var property in row)
			{
				if (seen.Add(property.Key))
				{
					ordered.Add(property.Key);
				}
			}
		}

		if (ordered.Count == 0)
		{
			return false;
		}

		columns = [.. ordered];
		rows = converted;
		return true;
	}

	/// <summary>
	/// The cell for <paramref name="column"/>: empty when the row does not have the key. Matched ordinally, the
	/// way the columns were collected, even in a row built with case-insensitive property names.
	/// </summary>
	public static string Cell(JsonObject? row, string column)
	{
		if (row is null || row.IndexOf(column) is not (>= 0 and var index))
		{
			return string.Empty;
		}

		var property = row.GetAt(index);
		return string.Equals(property.Key, column, StringComparison.Ordinal) ? Literal(property.Value) : string.Empty;
	}

	// Format characters can only appear inside a JSON string here, where \uXXXX is the same character
	// to a JSON reader. Returns the input unchanged — no allocation — when it has none.
	private static string EscapeFormatCharacters(string text)
	{
		var index = IndexOfFormatCharacter(text);
		if (index < 0)
		{
			return text;
		}

		var builder = new StringBuilder(text.Length + 12);
		builder.Append(text, 0, index);
		Span<char> units = stackalloc char[2];
		foreach (var rune in text.AsSpan(index).EnumerateRunes())
		{
			var written = rune.EncodeToUtf16(units);
			if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
			{
				builder.Append(units[..written]);
				continue;
			}

			for (var i = 0; i < written; i++)
			{
				builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)units[i]:X4}");
			}
		}

		return builder.ToString();
	}

	private static int IndexOfFormatCharacter(string text)
	{
		var index = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
			{
				return index;
			}

			index += rune.Utf16SequenceLength;
		}

		return -1;
	}
}
