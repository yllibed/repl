using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;
using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Dispatches MCP tool calls through the Repl pipeline.
/// Each call creates CLI tokens from the route template + JSON arguments,
/// then executes through standard Repl routing, binding, and middleware.
/// </summary>
internal sealed partial class McpToolAdapter
{
	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplDocCommand> _toolRoutes = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _staticToolResults = new(StringComparer.OrdinalIgnoreCase);

	public McpToolAdapter(ICoreReplApp app, ReplMcpServerOptions options, IServiceProvider services)
	{
		_app = app;
		_options = options;
		_services = services;
	}

	/// <summary>
	/// Clears all registered routes. Called before rebuilding on routing invalidation.
	/// </summary>
	public void ClearRoutes()
	{
		_toolRoutes.Clear();
		_staticToolResults.Clear();
	}

	/// <summary>
	/// Atomically replaces all routes from another adapter instance.
	/// Used for optimistic concurrency during routing invalidation.
	/// </summary>
	public void ReplaceRoutes(McpToolAdapter source)
	{
		_toolRoutes.Clear();
		_staticToolResults.Clear();
		foreach (var (key, value) in source._toolRoutes)
		{
			_toolRoutes[key] = value;
		}

		foreach (var (key, value) in source._staticToolResults)
		{
			_staticToolResults[key] = value;
		}
	}

	/// <summary>
	/// Registers a tool name → command mapping for dispatch.
	/// </summary>
	public void RegisterRoute(string toolName, ReplDocCommand command)
	{
		_toolRoutes[toolName] = command;
	}

	/// <summary>
	/// Registers a static text result for launcher-style tools.
	/// </summary>
	public void RegisterStaticResult(string toolName, string text)
	{
		_staticToolResults[toolName] = text;
	}

	/// <summary>
	/// Invokes a Repl command through the pipeline for an MCP tool call.
	/// </summary>
	public async Task<CallToolResult> InvokeAsync(
		string toolName,
		IDictionary<string, JsonElement> arguments,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct,
		bool allowStaticResults = true)
	{
		if (allowStaticResults && _staticToolResults.TryGetValue(toolName, out var staticResult))
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = staticResult }],
			};
		}

		if (!_toolRoutes.TryGetValue(toolName, out var command))
		{
			return ErrorResult($"Unknown tool: {toolName}");
		}

		var (tokens, prefills) = PrepareExecution(command.Path, arguments);
		return await ExecuteThroughPipelineAsync(tokens, prefills, server, progressToken, ct)
			.ConfigureAwait(false);
	}

	private async Task<CallToolResult> ExecuteThroughPipelineAsync(
		List<string> tokens,
		Dictionary<string, string> prefills,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct)
	{
		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP tool adapter requires CoreReplApp.");

		var outputWriter = new StringWriter();
		var inputReader = new StringReader(string.Empty);
		var feedback = _services.GetService(typeof(IMcpFeedback)) as IMcpFeedback;
		var interactionChannel = new McpInteractionChannel(
			prefills, _options.InteractivityMode, server, progressToken, feedback);
		var mcpServices = new McpServiceProviderOverlay(
			_services,
			new Dictionary<Type, object>
			{
				[typeof(IReplInteractionChannel)] = interactionChannel,
			});
		using var feedbackScope = (_services.GetService(typeof(IMcpFeedback)) as McpFeedbackService)
			?.PushProgressToken(progressToken);

		// Force JSON output — agents consume structured data, not human tables/banners.
		var effectiveTokens = new List<string>(tokens.Count + 1) { "--output:json" };
		effectiveTokens.AddRange(tokens);

		using (ReplSessionIO.SetSession(
			output: outputWriter,
			input: inputReader,
			ansiMode: Rendering.AnsiMode.Never,
			sessionId: $"mcp-{Guid.NewGuid():N}",
			isHostedSession: true))
		{
			ReplSessionIO.IsProgrammatic = true;
			var exitCode = await coreApp.RunSubInvocationAsync(
				effectiveTokens.ToArray(), mcpServices, ct).ConfigureAwait(false);

			var output = outputWriter.ToString().Trim();
			if (string.IsNullOrWhiteSpace(output))
			{
				output = exitCode == 0 ? "OK" : $"Command failed with exit code {exitCode}.";
			}

			return BuildToolResult(output, exitCode);
		}
	}

	private static CallToolResult BuildToolResult(string output, int exitCode)
	{
		if (exitCode == 0 && TryCreatePagedStructuredResult(output, out var structuredContent, out var summary))
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = summary }],
				StructuredContent = structuredContent,
				IsError = false,
			};
		}

		return new CallToolResult
		{
			Content = [new TextContentBlock { Text = output }],
			IsError = exitCode != 0,
		};
	}

	private static bool TryCreatePagedStructuredResult(
		string output,
		out JsonElement structuredContent,
		out string summary)
	{
		structuredContent = default;
		summary = string.Empty;
		try
		{
			using var document = JsonDocument.Parse(output);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("$type", out var type)
				|| type.ValueKind != JsonValueKind.String
				|| !string.Equals(type.GetString(), "page", StringComparison.Ordinal)
				|| !root.TryGetProperty("items", out var items)
				|| items.ValueKind != JsonValueKind.Array
				|| !root.TryGetProperty("pageInfo", out var pageInfo)
				|| pageInfo.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			structuredContent = root.Clone();
			summary = BuildPagedSummary(items.GetArrayLength(), pageInfo);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static string BuildPagedSummary(int count, JsonElement pageInfo)
	{
		var summary = $"Returned {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s).";
		if (pageInfo.TryGetProperty("totalCount", out var totalCount)
			&& totalCount.ValueKind == JsonValueKind.Number
			&& totalCount.TryGetInt64(out var total))
		{
			summary += $" Total: {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
		}

		if (pageInfo.TryGetProperty("nextCursor", out var nextCursor)
			&& nextCursor.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(nextCursor.GetString()))
		{
			summary += $" Continue with {McpResultFlowArgumentNames.Cursor}; cursor available in structured content.";
		}

		return summary;
	}

	internal static (List<string> Tokens, Dictionary<string, string> Prefills) PrepareExecution(
		string routePath,
		IDictionary<string, JsonElement> arguments)
	{
		var stringArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		var prefills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var resultFlowTokens = new List<string>();

		foreach (var (key, value) in arguments)
		{
			var strValue = value.ValueKind == JsonValueKind.String
				? value.GetString() ?? ""
				: value.GetRawText();

			if (key.StartsWith("answer.", StringComparison.OrdinalIgnoreCase))
			{
				prefills[key["answer.".Length..]] = strValue;
			}
			else if (string.Equals(key, McpResultFlowArgumentNames.Cursor, StringComparison.Ordinal))
			{
				ValidateResultCursor(strValue);
				resultFlowTokens.Add("--result:cursor");
				resultFlowTokens.Add(strValue);
			}
			else if (string.Equals(key, McpResultFlowArgumentNames.PageSize, StringComparison.Ordinal))
			{
				ValidateResultPageSize(strValue);
				resultFlowTokens.Add("--result:page-size");
				resultFlowTokens.Add(strValue);
			}
			else
			{
				stringArgs[key] = strValue;
			}
		}

		var tokens = ReconstructTokens(routePath, stringArgs);
		tokens.InsertRange(0, resultFlowTokens);
		return (tokens, prefills);
	}

	private static void ValidateResultCursor(string cursor)
	{
		if (cursor.Length > 512)
		{
			throw new InvalidOperationException("The MCP result cursor cannot exceed 512 characters.");
		}

		if (cursor.Length > 0 && cursor[0] == '-')
		{
			throw new InvalidOperationException("The MCP result cursor cannot start like a CLI option.");
		}

		if (cursor.Any(char.IsWhiteSpace))
		{
			throw new InvalidOperationException("The MCP result cursor cannot contain whitespace.");
		}
	}

	private static void ValidateResultPageSize(string pageSize)
	{
		if (pageSize.Length > 20)
		{
			throw new InvalidOperationException("The MCP result page size cannot exceed 20 characters.");
		}

		if (pageSize.Length == 0 || pageSize.Any(static c => c < '0' || c > '9'))
		{
			throw new InvalidOperationException("The MCP result page size must be numeric.");
		}
	}

	/// <summary>
	/// Reconstructs CLI tokens from a route template and MCP arguments.
	/// </summary>
	internal static List<string> ReconstructTokens(
		string routePath,
		IDictionary<string, object?> arguments)
	{
		var tokens = new List<string>();
		var consumedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var part in routePath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			var match = DynamicSegmentPattern().Match(part);
			if (match.Success)
			{
				var argName = match.Groups["name"].Value;
				consumedArgs.Add(argName);
				if (arguments.TryGetValue(argName, out var value) && value is not null)
				{
					var strValue = value.ToString() ?? "";
					if (strValue.Length > 0)
					{
						tokens.Add(strValue);
					}

					// Omit token entirely for missing optional segments.
				}
			}
			else
			{
				tokens.Add(part);
			}
		}

		// Remaining arguments become named options.
		// Note: answer: prefixes are separated by PrepareExecution upstream
		// and never reach this method.
		foreach (var (key, value) in arguments)
		{
			if (!consumedArgs.Contains(key))
			{
				tokens.Add($"--{key}");
				tokens.Add(value?.ToString() ?? "");
			}
		}

		return tokens;
	}

	private static CallToolResult ErrorResult(string message) => new()
	{
		Content = [new TextContentBlock { Text = message }],
		IsError = true,
	};

	[GeneratedRegex(@"^\{(?<name>[^:{}?]+)(?:\?)?(?::[^{}:]+)?\}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex DynamicSegmentPattern();
}
