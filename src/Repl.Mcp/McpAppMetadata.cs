using System.Text.Json;
using System.Text.Json.Nodes;

namespace Repl.Mcp;

internal static class McpAppMetadata
{
	public const string CommandMetadataKey = "Repl.Mcp.App";
	public const string ResourceMetadataKey = "Repl.Mcp.AppResource";
	public const string ExtensionName = "io.modelcontextprotocol/ui";

	public static JsonObject BuildToolMeta(McpAppToolOptions options)
	{
		var ui = new JsonObject
		{
			["resourceUri"] = options.ResourceUri,
			["visibility"] = BuildVisibilityArray(options.Visibility),
		};

		return new JsonObject { ["ui"] = ui };
	}

	public static JsonObject? BuildResourceMeta(McpAppResourceOptions options)
	{
		var ui = new JsonObject();
		foreach (var (key, value) in options.UiMetadata)
		{
			if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
			{
				ui[key] = value;
			}
		}

		if (BuildCsp(options.Csp) is { } csp)
		{
			ui["csp"] = csp;
		}

		if (BuildPermissions(options.Permissions) is { } permissions)
		{
			ui["permissions"] = permissions;
		}

		if (!string.IsNullOrWhiteSpace(options.Domain))
		{
			ui["domain"] = options.Domain;
		}

		if (options.PrefersBorder is { } prefersBorder)
		{
			ui["prefersBorder"] = prefersBorder;
		}

		if (!string.IsNullOrWhiteSpace(options.PreferredDisplayMode))
		{
			ui["preferredDisplayMode"] = options.PreferredDisplayMode;
		}

		return ui.Count == 0
			? null
			: new JsonObject { ["ui"] = ui };
	}

	private static JsonArray BuildVisibilityArray(McpAppVisibility visibility)
	{
		var values = new List<string>();
		if (visibility.HasFlag(McpAppVisibility.Model))
		{
			values.Add("model");
		}

		if (visibility.HasFlag(McpAppVisibility.App))
		{
			values.Add("app");
		}

		return JsonSerializer.SerializeToNode(
			values.ToArray(),
			McpJsonContext.Default.StringArray)!.AsArray();
	}

	private static JsonObject? BuildCsp(McpAppCsp? csp)
	{
		if (csp is null)
		{
			return null;
		}

		var node = new JsonObject();
		AddStringArray(node, "connectDomains", csp.ConnectDomains);
		AddStringArray(node, "resourceDomains", csp.ResourceDomains);
		AddStringArray(node, "frameDomains", csp.FrameDomains);
		AddStringArray(node, "baseUriDomains", csp.BaseUriDomains);
		return node.Count == 0 ? null : node;
	}

	private static JsonObject? BuildPermissions(McpAppPermissions? permissions)
	{
		if (permissions is null)
		{
			return null;
		}

		var node = new JsonObject();
		AddPermission(node, "camera", permissions.Camera);
		AddPermission(node, "microphone", permissions.Microphone);
		AddPermission(node, "geolocation", permissions.Geolocation);
		AddPermission(node, "clipboardWrite", permissions.ClipboardWrite);
		return node.Count == 0 ? null : node;
	}

	private static void AddPermission(JsonObject node, string propertyName, bool value)
	{
		if (value)
		{
			node[propertyName] = new JsonObject();
		}
	}

	private static void AddStringArray(JsonObject node, string propertyName, IReadOnlyList<string>? values)
	{
		if (values is null || values.Count == 0)
		{
			return;
		}

		var normalized = values
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.ToArray();

		if (normalized.Length > 0)
		{
			node[propertyName] = JsonSerializer.SerializeToNode(
				normalized,
				McpJsonContext.Default.StringArray);
		}
	}
}
