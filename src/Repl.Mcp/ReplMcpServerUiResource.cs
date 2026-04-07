using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

internal sealed partial class ReplMcpServerUiResource : McpServerResource
{
	private readonly string _resourceName;
	private readonly McpToolAdapter _adapter;
	private readonly McpAppCommandResourceOptions _options;
	private readonly ResourceTemplate _protocolResourceTemplate;
	private readonly Regex? _uriParser;
	private readonly string[] _variableNames;

	public ReplMcpServerUiResource(
		ReplDocCommand command,
		string resourceName,
		McpAppCommandResourceOptions options,
		McpToolAdapter adapter)
	{
		_resourceName = resourceName;
		_adapter = adapter;
		_options = options;
		_protocolResourceTemplate = new ResourceTemplate
		{
			Name = options.ResourceOptions.Name ?? BuildDefaultResourceName(command.Path),
			Description = options.ResourceOptions.Description ?? command.Description,
			UriTemplate = options.ResourceUri,
			MimeType = McpAppValidation.ResourceMimeType,
			Meta = McpAppMetadata.BuildResourceMeta(options.ResourceOptions),
		};

		_variableNames = BuildUriParser(options.ResourceUri, out _uriParser);
	}

	public override ResourceTemplate ProtocolResourceTemplate => _protocolResourceTemplate;

	public override IReadOnlyList<object> Metadata { get; } = [];

	public override bool IsMatch(string uri)
	{
		ArgumentNullException.ThrowIfNull(uri);

		if (_uriParser is not null)
		{
			return _uriParser.IsMatch(uri);
		}

		return string.Equals(uri, _options.ResourceUri, StringComparison.OrdinalIgnoreCase);
	}

	public override async ValueTask<ReadResourceResult> ReadAsync(
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		var arguments = ExtractArguments(request.Params.Uri);

		var result = await _adapter.InvokeAsync(
				_resourceName,
				arguments,
				request.Server,
				progressToken: null,
				cancellationToken,
				allowStaticResults: false)
			.ConfigureAwait(false);

		if (result.IsError == true)
		{
			var errorText = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
				?? "UI resource read failed.";
			throw new McpException(errorText);
		}

		var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
		return new ReadResourceResult
		{
			Contents =
			[
				new TextResourceContents
				{
					Uri = request.Params.Uri,
					MimeType = McpAppValidation.ResourceMimeType,
					Text = UnwrapJsonString(text),
					Meta = McpAppMetadata.BuildResourceMeta(_options.ResourceOptions),
				},
			],
		};
	}

	private Dictionary<string, JsonElement> ExtractArguments(string uri)
	{
		var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

		if (_uriParser is null)
		{
			return arguments;
		}

		var match = _uriParser.Match(uri);
		if (!match.Success)
		{
			return arguments;
		}

		foreach (var pair in _variableNames
			.Select(name => (Name: name, Group: match.Groups[name]))
			.Where(pair => pair.Group.Success))
		{
			var value = Uri.UnescapeDataString(pair.Group.Value);
			arguments[pair.Name] = JsonSerializer.SerializeToElement(value, McpJsonContext.Default.String);
		}

		return arguments;
	}

	private static string[] BuildUriParser(string uriTemplate, out Regex? parser)
	{
		var variableNames = new List<string>();
		var regexParts = new System.Text.StringBuilder("^");

		var remaining = uriTemplate.AsSpan();
		while (remaining.Length > 0)
		{
			var braceIndex = remaining.IndexOf('{');
			if (braceIndex < 0)
			{
				regexParts.Append(Regex.Escape(remaining.ToString()));
				break;
			}

			if (braceIndex > 0)
			{
				regexParts.Append(Regex.Escape(remaining[..braceIndex].ToString()));
			}

			var closeIndex = remaining.IndexOf('}');
			var name = remaining[(braceIndex + 1)..closeIndex].ToString();
			variableNames.Add(name);
			regexParts.Append($"(?<{name}>[^/]+)");
			remaining = remaining[(closeIndex + 1)..];
		}

		regexParts.Append('$');

		if (variableNames.Count == 0)
		{
			parser = null;
			return [];
		}

		parser = new Regex(
			regexParts.ToString(),
			RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
			TimeSpan.FromSeconds(1));
		return [.. variableNames];
	}

	private static string UnwrapJsonString(string text)
	{
		if (text.Length == 0 || text[0] != '"')
		{
			return text;
		}

		try
		{
			return JsonSerializer.Deserialize(text, McpJsonContext.Default.String) ?? text;
		}
		catch (JsonException)
		{
			return text;
		}
	}

	private static string BuildDefaultResourceName(string path)
	{
		var parts = path
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static segment => segment.Length == 0 || segment[0] != '{')
			.ToArray();
		return parts.Length == 0 ? path : string.Join(' ', parts);
	}
}
