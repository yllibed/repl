using System.Text;
using System.Text.Json;

namespace Repl;

internal sealed class YamlOutputTransformer(JsonSerializerOptions serializerOptions) : IOutputTransformer
{
	public string Name => "yaml";

	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (value is null)
		{
			return ValueTask.FromResult(string.Empty);
		}

#pragma warning disable IL2026 // YAML output is built from runtime models through JSON shape serialization.
		var json = JsonSerializer.SerializeToElement(value, serializerOptions);
#pragma warning restore IL2026
		var builder = new StringBuilder();
		WriteElement(builder, json, 0, cancellationToken);
		return ValueTask.FromResult(builder.ToString().TrimEnd());
	}

	private static void WriteElement(
		StringBuilder builder,
		JsonElement element,
		int indent,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Split scalar vs complex nodes early to keep indentation rules centralized.
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				WriteObject(builder, element, indent, cancellationToken);
				return;
			case JsonValueKind.Array:
				WriteArray(builder, element, indent, cancellationToken);
				return;
			default:
				builder.Append(FormatScalar(element));
				builder.AppendLine();
				return;
		}
	}

	private static void WriteObject(
		StringBuilder builder,
		JsonElement element,
		int indent,
		CancellationToken cancellationToken)
	{
		foreach (var property in element.EnumerateObject())
		{
			cancellationToken.ThrowIfCancellationRequested();
			AppendIndent(builder, indent);
			builder.Append(property.Name);
			builder.Append(':');

			if (IsScalar(property.Value))
			{
				builder.Append(' ');
				builder.Append(FormatScalar(property.Value));
				builder.AppendLine();
				continue;
			}

			builder.AppendLine();
			WriteElement(builder, property.Value, indent + 2, cancellationToken);
		}
	}

	private static void WriteArray(
		StringBuilder builder,
		JsonElement element,
		int indent,
		CancellationToken cancellationToken)
	{
		var hasAny = false;
		foreach (var item in element.EnumerateArray())
		{
			hasAny = true;
			cancellationToken.ThrowIfCancellationRequested();
			AppendIndent(builder, indent);
			builder.Append('-');

			if (IsScalar(item))
			{
				builder.Append(' ');
				builder.Append(FormatScalar(item));
				builder.AppendLine();
				continue;
			}

			builder.AppendLine();
			WriteElement(builder, item, indent + 2, cancellationToken);
		}

		if (!hasAny)
		{
			AppendIndent(builder, indent);
			builder.AppendLine("[]");
		}
	}

	private static bool IsScalar(JsonElement element) =>
		element.ValueKind is JsonValueKind.String
		or JsonValueKind.Number
		or JsonValueKind.True
		or JsonValueKind.False
		or JsonValueKind.Null
		or JsonValueKind.Undefined;

	private static string FormatScalar(JsonElement element) =>
		element.ValueKind switch
		{
			JsonValueKind.String => QuoteString(element.GetString() ?? string.Empty),
			JsonValueKind.Number => element.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => "null",
		};

	private static string QuoteString(string value)
	{
		// Always quote strings to avoid YAML implicit type coercion surprises.
		return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
	}

	private static void AppendIndent(StringBuilder builder, int indent)
	{
		if (indent <= 0)
		{
			return;
		}

		builder.Append(' ', indent);
	}
}
