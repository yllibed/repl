using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl;
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
	private const int ProgrammaticInvocationContractVersion = 1;
	internal const string ForcedOutputFormat = "json";
	private const string TextPlainMimeType = "text/plain";

	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;
	private readonly McpRequestServerAccessor _requestServers;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplDocCommand> _toolRoutes = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _staticToolResults = new(StringComparer.OrdinalIgnoreCase);

	public McpToolAdapter(
		ICoreReplApp app,
		ReplMcpServerOptions options,
		IServiceProvider services,
		McpRequestServerAccessor requestServers)
	{
		_app = app;
		_options = options;
		_services = services;
		_requestServers = requestServers;
	}

	/// <summary>
	/// Binds the flowing async context to <paramref name="request"/> before dispatching a command.
	/// </summary>
	/// <remarks>
	/// The pre-built primitives (<see cref="ReplMcpServerTool"/> and friends) are dispatched straight
	/// by the SDK on the <c>BuildMcpServerOptions</c> path, bypassing <see cref="McpServerHandler"/>'s
	/// request handlers entirely. Without this, capability services resolved from DI would have no
	/// request to resolve against and would report every client capability as unavailable.
	/// </remarks>
	internal void BindRequest(MessageContext request) => _requestServers.BindRequest(request);

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

		return BuildToolResult(
			output, invocation.ExitCode, _options.PagedResultTextMode, invocation.UndeliveredMessages);
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
		var feedbackService = _services.GetService(typeof(IMcpFeedback)) as McpFeedbackService;
		using var feedbackScope = feedbackService?.PushProgressToken(progressToken);
		// Messages the client cannot receive as notifications ride back in the tool result instead,
		// so no feedback is lost on a request that never asked for log notifications.
		using var undeliveredScope = feedbackService?.PushUndeliveredMessages();

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
			using var invocationContract = ReplSessionIO.PushProgrammaticInvocationContract(ProgrammaticInvocationContractVersion);
			var exitCode = await invocableApp.RunSubInvocationAsync(
				effectiveTokens.ToArray(), mcpServices, ct).ConfigureAwait(false);

			var output = outputWriter.ToString().Trim();
			var error = captureCommandOutput ? string.Empty : errorWriter.ToString().Trim();
			var undelivered = undeliveredScope?.Messages.Drain() ?? [];
			return new McpPipelineInvocation(output, error, exitCode, undelivered);
		}
	}

	internal readonly record struct McpResourceReadInvocation(string Text, string MimeType, bool IsError);

	private readonly record struct McpPipelineInvocation(
		string Output,
		string Error,
		int ExitCode,
		IReadOnlyList<string> UndeliveredMessages);

	private static CallToolResult BuildToolResult(
		string output,
		int exitCode,
		McpPagedResultTextMode pagedTextMode,
		IReadOnlyList<string> undeliveredMessages)
	{
		if (exitCode == 0 && TryCreatePagedStructuredResult(output, out var structuredContent, out var summary))
		{
			return new CallToolResult
			{
				Content = WithMessages(
					new TextContentBlock { Text = BuildPagedTextContent(output, summary, pagedTextMode) },
					undeliveredMessages),
				StructuredContent = structuredContent,
				IsError = false,
			};
		}

		return new CallToolResult
		{
			Content = WithMessages(new TextContentBlock { Text = output }, undeliveredMessages),
			IsError = exitCode != 0,
		};
	}

	/// <summary>
	/// Appends messages the client could not receive as notifications, as a trailing content block.
	/// </summary>
	/// <remarks>
	/// The command's own payload stays the FIRST block (and <c>StructuredContent</c> is untouched), so
	/// a caller reading the primary result is unaffected. Only requests that asked for no log level
	/// carry anything here — a client receiving message notifications would otherwise see each one
	/// twice. Resource reads deliberately get no such block: their body must match the advertised
	/// MIME type.
	/// </remarks>
	private static List<ContentBlock> WithMessages(
		TextContentBlock primary,
		IReadOnlyList<string> undeliveredMessages)
	{
		if (undeliveredMessages.Count == 0)
		{
			return [primary];
		}

		var blocks = new List<ContentBlock>(undeliveredMessages.Count + 1) { primary };
		foreach (var message in undeliveredMessages)
		{
			blocks.Add(new TextContentBlock { Text = message });
		}

		return blocks;
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
		var optionTokens = command.Options.ToDictionary(
			static option => option.Name,
			static option => option.Aliases.Count > 0 ? option.Aliases[0] : $"--{option.Name}",
			StringComparer.Ordinal);
		// A bool-flag option's value token is only ever consumed on a best-effort basis: when it
		// looks like a fresh option token, ApplyBoolFlagValue declines it WITHOUT a diagnostic
		// (that decline is required so legitimate flag-chaining like "--verbose --other" keeps
		// working), leaving it to be re-lexed as its own token on the parser's next iteration —
		// which can bind a Hidden() option's alias. Every other option kind either has no value to
		// smuggle or fails the whole call with a diagnostic when its value looks option-like, so
		// only bool options need the inline "--name=value" form that makes re-lexing impossible.
		var boolOptionNames = command.Options
			.Where(static option => IsBooleanTypeName(option.Type))
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.Ordinal);
		return PrepareExecution(command.Path, arguments, allowedArgumentNames, optionTokens, boolOptionNames);
	}

	private static (List<string> Tokens, Dictionary<string, string> Prefills) PrepareExecution(
		string routePath,
		IDictionary<string, JsonElement> arguments,
		AllowedArgumentNames? allowedArgumentNames,
		IReadOnlyDictionary<string, string> optionTokens,
		IReadOnlySet<string>? boolOptionNames = null)
	{
		var stringArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
		var prefills = new Dictionary<string, string>(StringComparer.Ordinal);
		var resultFlowTokens = new List<string>();
		var suppliedSchemaFields = new HashSet<string>(StringComparer.Ordinal);

		foreach (var (requestedKey, value) in arguments)
		{
			var field = allowedArgumentNames?.Resolve(requestedKey)
				?? new AllowedArgumentField(requestedKey, McpArgumentFieldKind.Command);
			var key = field.Name;
			if (!suppliedSchemaFields.Add(key))
			{
				throw new InvalidOperationException(
					$"The MCP argument '{requestedKey}' duplicates the schema field '{key}'.");
			}
			var strValue = value.ValueKind == JsonValueKind.String
				? value.GetString() ?? ""
				: value.GetRawText();

			switch (field.Kind)
			{
				case McpArgumentFieldKind.Answer:
					prefills.Add(key["answer.".Length..], strValue);
					break;
				case McpArgumentFieldKind.Cursor:
					ValidateResultCursor(strValue);
					resultFlowTokens.Add(ReplResultFlowOptionNames.Cursor);
					resultFlowTokens.Add(strValue);
					break;
				case McpArgumentFieldKind.PageSize:
					ValidateResultPageSize(strValue);
					resultFlowTokens.Add(ReplResultFlowOptionNames.PageSize);
					resultFlowTokens.Add(strValue);
					break;
				default:
					ValidateCommandArgumentValue(strValue);
					stringArgs.Add(key, strValue);
					break;
			}
		}

		var tokens = ReconstructTokens(routePath, stringArgs, optionTokens, boolOptionNames);
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

	internal static void ValidateArgumentNames(ReplDocCommand command) =>
		_ = BuildAllowedArgumentNames(command);

	private static AllowedArgumentNames BuildAllowedArgumentNames(ReplDocCommand command)
	{
		var names = new AllowedArgumentNames();
		foreach (var argument in command.Arguments)
		{
			names.Add(argument.Name, McpArgumentFieldKind.Command);
		}

		foreach (var option in command.Options)
		{
			names.Add(option.Name, McpArgumentFieldKind.Command);
		}

		if (command.Answers is { Count: > 0 })
		{
			foreach (var answer in command.Answers)
			{
				names.Add($"answer.{answer.Name}", McpArgumentFieldKind.Answer);
			}
		}

		if (command.AcceptsPagingInput || command.EmitsPagedResult)
		{
			names.Add(McpResultFlowArgumentNames.Cursor, McpArgumentFieldKind.Cursor);
			names.Add(McpResultFlowArgumentNames.PageSize, McpArgumentFieldKind.PageSize);
		}

		return names;
	}

	private enum McpArgumentFieldKind
	{
		Command,
		Answer,
		Cursor,
		PageSize,
	}

	private readonly record struct AllowedArgumentField(string Name, McpArgumentFieldKind Kind);

	private sealed class AllowedArgumentNames
	{
		private readonly Dictionary<string, AllowedArgumentField> _exactFields = new(StringComparer.Ordinal);
		private readonly List<AllowedArgumentField> _orderedFields = [];

		internal void Add(string name, McpArgumentFieldKind kind)
		{
			var field = new AllowedArgumentField(name, kind);
			if (!_exactFields.TryAdd(name, field))
			{
				throw new InvalidOperationException(
					$"MCP argument name collision: '{name}' is declared more than once by the tool schema.");
			}

			_orderedFields.Add(field);
		}

		internal AllowedArgumentField Resolve(string requestedName)
		{
			if (_exactFields.TryGetValue(requestedName, out var exact))
			{
				return exact;
			}

			var matches = _orderedFields
				.Where(field => string.Equals(field.Name, requestedName, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			return matches.Length switch
			{
				0 => throw new InvalidOperationException(
					$"The MCP argument '{requestedName}' is not defined by the tool schema."),
				1 => matches[0],
				_ => throw new InvalidOperationException(
					$"The MCP argument '{requestedName}' is ambiguous between schema fields "
					+ $"{string.Join(", ", matches.Select(static field => $"'{field.Name}'"))}."),
			};
		}
	}

	/// <summary>
	/// Reconstructs CLI tokens from a route template and MCP arguments.
	/// </summary>
	internal static List<string> ReconstructTokens(
		string routePath,
		IDictionary<string, object?> arguments) =>
		ReconstructTokens(routePath, arguments, optionTokens: null, boolOptionNames: null);

	private static List<string> ReconstructTokens(
		string routePath,
		IDictionary<string, object?> arguments,
		IReadOnlyDictionary<string, string>? optionTokens,
		IReadOnlySet<string>? boolOptionNames)
	{
		var tokens = new List<string>();
		var consumedArgs = new HashSet<string>(
			optionTokens is null ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
						ValidatePositionalArgumentValue(strValue);
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
				var token = ResolveOptionToken(key, optionTokens);
				var strValue = value?.ToString() ?? "";
				if (boolOptionNames?.Contains(key) == true)
				{
					// Single inline token: TrySplitOptionToken (InvocationOptionParser) splits on
					// the first '=' only, so the value survives intact even when it starts with
					// '-' or contains '=' itself, and it can never be left dangling by
					// ApplyBoolFlagValue to be re-lexed as a fresh option on the next iteration.
					tokens.Add($"{token}={strValue}");
				}
				else
				{
					tokens.Add(token);
					tokens.Add(strValue);
				}
			}
		}

		return tokens;
	}

	// Guards the ONE place an MCP-supplied value becomes a bare CLI token: a positional route
	// segment. It has no separator that can escape the value the way an inline "--token=value"
	// pair does for a bool option, so a value that would itself be lexed as an option token (and
	// could then resolve to a route/global option — including a Hidden() one, since parsing still
	// accepts those — via the general-purpose parser) must be rejected here instead.
	private static void ValidatePositionalArgumentValue(string value)
	{
		if (InvocationOptionParser.LooksLikeOptionToken(value) && !InvocationOptionParser.IsSignedNumericLiteral(value))
		{
			throw new InvalidOperationException(
				"The MCP argument value cannot start like a CLI option because it fills a positional route segment, which has no way to escape it.");
		}
	}

	private static bool IsBooleanTypeName(string typeName) =>
		string.Equals(typeName, "bool", StringComparison.Ordinal)
		|| string.Equals(typeName, "bool?", StringComparison.Ordinal);

	private static string ResolveOptionToken(string key, IReadOnlyDictionary<string, string>? optionTokens)
	{
		if (optionTokens is null)
		{
			return $"--{key}";
		}

		if (optionTokens.TryGetValue(key, out var exactToken))
		{
			return exactToken;
		}

		string? matchedToken = null;
		foreach (var (optionName, token) in optionTokens)
		{
			if (!string.Equals(optionName, key, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (matchedToken is not null)
			{
				throw new InvalidOperationException(
					$"The MCP argument '{key}' is ambiguous because option names differ only by casing.");
			}

			matchedToken = token;
		}

		return matchedToken ?? $"--{key}";
	}

	private static CallToolResult ErrorResult(string message) => new()
	{
		Content = [new TextContentBlock { Text = message }],
		IsError = true,
	};

	[GeneratedRegex(@"^\{(?<name>[^:{}?]+)(?:\?)?(?::[^{}:]+)?\}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex DynamicSegmentPattern();
}
