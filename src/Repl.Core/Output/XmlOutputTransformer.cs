using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Collections;

namespace Repl;

internal sealed class XmlOutputTransformer(JsonSerializerOptions serializerOptions) : IOutputTransformer
{
	public string Name => "xml";

	public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (value is null)
		{
			return ValueTask.FromResult(string.Empty);
		}

		if (value is IEnumerable enumerable && value is not string)
		{
			return ValueTask.FromResult(ConvertTopLevelCollection(value, enumerable, cancellationToken).ToString());
		}

#pragma warning disable IL2026 // XML output is built from runtime models through JSON shape serialization.
		var json = JsonSerializer.SerializeToElement(value, serializerOptions);
#pragma warning restore IL2026
		var root = ConvertElement(ResolveRootElementName(value), json, cancellationToken);
		return ValueTask.FromResult(root.ToString());
	}

	private XElement ConvertTopLevelCollection(object source, IEnumerable enumerable, CancellationToken cancellationToken)
	{
		var root = new XElement(XmlConvert.EncodeLocalName(ResolveRootElementName(source)));
		foreach (var item in enumerable.Cast<object?>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (item is null)
			{
				root.Add(new XElement("item"));
				continue;
			}

#pragma warning disable IL2026 // XML output is built from runtime models through JSON shape serialization.
			var itemJson = JsonSerializer.SerializeToElement(item, serializerOptions);
#pragma warning restore IL2026
			root.Add(ConvertElement(ResolveTypeElementName(item.GetType(), fallbackName: "item"), itemJson, cancellationToken));
		}

		return root;
	}

	private static string ResolveRootElementName(object value)
	{
		var type = value.GetType();
		if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
		{
			return "value";
		}

		if (type.IsArray || (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
		{
			return "items";
		}

		var name = type.Name;
		var genericMarker = name.IndexOf('`');
		if (genericMarker >= 0)
		{
			name = name[..genericMarker];
		}

		if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<>", StringComparison.Ordinal))
		{
			return "result";
		}

		return name;
	}

	private static string ResolveTypeElementName(Type type, string fallbackName)
	{
		var name = type.Name;
		var genericMarker = name.IndexOf('`');
		if (genericMarker >= 0)
		{
			name = name[..genericMarker];
		}

		if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<>", StringComparison.Ordinal))
		{
			return fallbackName;
		}

		return name;
	}

	private static XElement ConvertElement(string name, JsonElement element, CancellationToken cancellationToken)
	{
		var safeName = XmlConvert.EncodeLocalName(name);
		// JSON values are mapped deterministically to XML so non-human formats stay machine-friendly.
		return element.ValueKind switch
		{
			JsonValueKind.Object => ConvertObject(safeName, element, cancellationToken),
			JsonValueKind.Array => ConvertArray(safeName, element, cancellationToken),
			JsonValueKind.String => new XElement(safeName, element.GetString() ?? string.Empty),
			JsonValueKind.Number => new XElement(safeName, element.GetRawText()),
			JsonValueKind.True => new XElement(safeName, "true"),
			JsonValueKind.False => new XElement(safeName, "false"),
			_ => new XElement(safeName),
		};
	}

	private static XElement ConvertObject(string name, JsonElement element, CancellationToken cancellationToken)
	{
		var node = new XElement(name);
		foreach (var property in element.EnumerateObject())
		{
			cancellationToken.ThrowIfCancellationRequested();
			node.Add(ConvertElement(property.Name, property.Value, cancellationToken));
		}

		return node;
	}

	private static XElement ConvertArray(string name, JsonElement element, CancellationToken cancellationToken)
	{
		var node = new XElement(name);
		foreach (var item in element.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			node.Add(ConvertElement("item", item, cancellationToken));
		}

		return node;
	}
}
