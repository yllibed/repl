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
	internal const string ForcedOutputFormat = "json";
	private const string TextPlainMimeType = "text/plain";

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

	internal string ForcedOutputMimeType
	{
		get
		{
			if (_app is not CoreReplApp coreApp)
			{
				throw new InvalidOperationException("MCP tool adapter requires a CoreReplApp to resolve output metadata.");
			}

			if (!coreApp.OptionsSnapshot.Output.Transformers.TryGetValue(ForcedOutputFormat, out var transformer))
			{
				throw new InvalidOperationException("MCP server requires the 'json' output transformer.");
			}

			return transformer.MimeType;
		}
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

		var (tokens, prefills) = PrepareExecution(command, arguments);
		var invocation = await ExecuteThroughPipelineAsync(tokens, prefills, server, progressToken, ct)
			.ConfigureAwait(false);
		var output = invocation.Output;
		if (string.IsNullOrWhiteSpace(output))
		{
			output = invocation.ExitCode == 0
				? "OK"
				: $"Command failed with exit code {invocation.ExitCode}.";
		}

		return BuildToolResult(output, invocation.ExitCode, _options.PagedResultTextMode);
	}

	internal async Task<McpResourceReadInvocation> InvokeResourceAsync(
		string resourceName,
		IDictionary<string, JsonElement> arguments,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct)
	{
		if (!_toolRoutes.TryGetValue(resourceName, out var command))
		{
			return new McpResourceReadInvocation($"Unknown resource: {resourceName}", TextPlainMimeType, IsError: true);
		}

		var (tokens, prefills) = PrepareExecution(command, arguments);
		var invocation = await ExecuteThroughPipelineAsync(
			tokens,
			prefills,
			server,
			progressToken,
			ct,
			captureCommandOutput: false)
			.ConfigureAwait(false);

		if (invocation.ExitCode != 0)
		{
			var error = invocation.Output;
			if (string.IsNullOrWhiteSpace(error))
			{
				error = invocation.Error;
			}

			if (string.IsNullOrWhiteSpace(error))
			{
				error = $"Command failed with exit code {invocation.ExitCode}.";
			}

			return new McpResourceReadInvocation(error, TextPlainMimeType, IsError: true);
		}

		if (string.IsNullOrWhiteSpace(invocation.Output))
		{
			// Results.Exit(0) without a payload intentionally renders no CLI output.
			// Resource reads still need a body that matches the advertised forced JSON MIME type.
			return new McpResourceReadInvocation("null", ForcedOutputMimeType, IsError: false);
		}

		return new McpResourceReadInvocation(invocation.Output, ForcedOutputMimeType, IsError: false);
	}

	private async Task<McpPipelineInvocation> ExecuteThroughPipelineAsync(
		List<string> tokens,
		Dictionary<string, string> prefills,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct,
		bool captureCommandOutput = true)
	{
		var invocableApp = _app as ISubInvocableReplApp
			?? throw new InvalidOperationException("MCP tool adapter requires an app that supports sub-invocation.");

		var outputWriter = new StringWriter();
		var errorWriter = captureCommandOutput ? outputWriter : new StringWriter();
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
		var effectiveTokens = new List<string>(tokens.Count + 1) { $"--output:{ForcedOutputFormat}" };
		effectiveTokens.AddRange(tokens);

		// Command-backed resources expose the rendered return value as the resource body.
		// Low-level handler writes to IReplIoContext.Output/Error are side-channel output, not resource content.
		var commandOutput = captureCommandOutput ? outputWriter : TextWriter.Null;
		using (ReplSessionIO.SetSession(
			output: outputWriter,
			input: inputReader,
			ansiMode: Rendering.AnsiMode.Never,
			sessionId: $"mcp-{Guid.NewGuid():N}",
			commandOutput: commandOutput,
			error: errorWriter,
			isHostedSession: true))
		{
			ReplSessionIO.IsProgrammatic = true;
			var exitCode = await invocableApp.RunSubInvocationAsync(
				effectiveTokens.ToArray(), mcpServices, ct).ConfigureAwait(false);

			var output = outputWriter.ToString().Trim();
			var error = captureCommandOutput ? string.Empty : errorWriter.ToString().Trim();
			return new McpPipelineInvocation(output, error, exitCode);
		}
	}

	internal readonly record struct McpResourceReadInvocation(string Text, string MimeType, bool IsError);

	private readonly record struct McpPipelineInvocation(string Output, string Error, int ExitCode);

	private static CallToolResult BuildToolResult(string output, int exitCode, McpPagedResultTextMode pagedTextMode)
	{
		if (exitCode == 0 && TryCreatePagedStructuredResult(output, out var structuredContent, out var summary))
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = BuildPagedTextContent(output, summary, pagedTextMode) }],
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

	private static string BuildPagedTextContent(
		string serializedPage,
		string summary,
		McpPagedResultTextMode mode) =>
		mode switch
		{
			McpPagedResultTextMode.SummaryOnly => summary,
			McpPagedResultTextMode.SummaryAndSerializedJson => string.Concat(summary, Environment.NewLine, serializedPage),
			_ => serializedPage,
		};

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
				|| !root.TryGetProperty(ReplPageWireNames.Type, out var type)
				|| type.ValueKind != JsonValueKind.String
				|| !string.Equals(type.GetString(), ReplPageWireNames.PageType, StringComparison.Ordinal)
				|| !root.TryGetProperty(ReplPageWireNames.Items, out var items)
				|| items.ValueKind != JsonValueKind.Array
				|| !root.TryGetProperty(ReplPageWireNames.PageInfo, out var pageInfo)
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
		if (pageInfo.TryGetProperty(ReplPageWireNames.TotalCount, out var totalCount)
			&& totalCount.ValueKind == JsonValueKind.Number
			&& totalCount.TryGetInt64(out var total))
		{
			summary += $" Total: {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
		}

		if (pageInfo.TryGetProperty(ReplPageWireNames.NextCursor, out var nextCursor)
			&& nextCursor.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(nextCursor.GetString()))
		{
			summary += $" Continue with {McpResultFlowArgumentNames.Cursor}; cursor available in structured content.";
		}

		return summary;
	}

	internal static (List<string> Tokens, Dictionary<string, string> Prefills) PrepareExecution(
		ReplDocCommand command,
		IDictionary<string, JsonElement> arguments)
	{
		var allowedArgumentNames = BuildAllowedArgumentNames(command);
		return PrepareExecution(command.Path, arguments, allowedArgumentNames);
	}

	private static (List<string> Tokens, Dictionary<string, string> Prefills) PrepareExecution(
		string routePath,
		IDictionary<string, JsonElement> arguments,
		HashSet<string>? allowedArgumentNames)
	{
		var stringArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		var prefills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var resultFlowTokens = new List<string>();

		foreach (var (key, value) in arguments)
		{
			var strValue = value.ValueKind == JsonValueKind.String
				? value.GetString() ?? ""
				: value.GetRawText();

			if (allowedArgumentNames is not null && !allowedArgumentNames.Contains(key))
			{
				throw new InvalidOperationException(
					$"The MCP argument '{key}' is not defined by the tool schema.");
			}

			if (key.StartsWith("answer.", StringComparison.OrdinalIgnoreCase))
			{
				prefills[key["answer.".Length..]] = strValue;
			}
			else if (string.Equals(key, McpResultFlowArgumentNames.Cursor, StringComparison.OrdinalIgnoreCase))
			{
				ValidateResultCursor(strValue);
				resultFlowTokens.Add(ReplResultFlowOptionNames.Cursor);
				resultFlowTokens.Add(strValue);
			}
			else if (string.Equals(key, McpResultFlowArgumentNames.PageSize, StringComparison.OrdinalIgnoreCase))
			{
				ValidateResultPageSize(strValue);
				resultFlowTokens.Add(ReplResultFlowOptionNames.PageSize);
				resultFlowTokens.Add(strValue);
			}
			else
			{
				ValidateCommandArgumentValue(strValue);
				stringArgs[key] = strValue;
			}
		}

		var tokens = ReconstructTokens(routePath, stringArgs);
		tokens.InsertRange(0, resultFlowTokens);
		return (tokens, prefills);
	}

	private static void ValidateResultCursor(string cursor)
		=> ResultFlowCursorPolicy.ValidateOrThrow(cursor);

	private static void ValidateResultPageSize(string pageSize)
	{
		if (pageSize.Length > 10)
		{
			throw new InvalidOperationException("The MCP result page size cannot exceed 10 characters.");
		}

		if (pageSize.Length == 0 || pageSize.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
		{
			throw new InvalidOperationException("The MCP result page size must be numeric.");
		}

		if (!int.TryParse(pageSize, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
			|| value <= 0)
		{
			throw new InvalidOperationException("The MCP result page size must fit in a positive 32-bit integer.");
		}
	}

	private static void ValidateCommandArgumentValue(string value)
	{
		if (value.StartsWith("--", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The MCP argument value cannot start like a CLI option.");
		}
	}

	private static HashSet<string> BuildAllowedArgumentNames(ReplDocCommand command)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var argument in command.Arguments)
		{
			names.Add(argument.Name);
		}

		foreach (var option in command.Options)
		{
			names.Add(option.Name);
		}

		if (command.Answers is { Count: > 0 })
		{
			foreach (var answer in command.Answers)
			{
				names.Add($"answer.{answer.Name}");
			}
		}

		if (command.AcceptsPagingInput || command.EmitsPagedResult)
		{
			names.Add(McpResultFlowArgumentNames.Cursor);
			names.Add(McpResultFlowArgumentNames.PageSize);
		}

		return names;
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
