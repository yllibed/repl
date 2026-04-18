using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Implements <see cref="IReplInteractionChannel"/> for MCP mode with progressive degradation.
/// Tier 1: Prefill from tool arguments. Tier 2: Elicitation. Tier 3: Sampling. Tier 4: Default/Fail.
/// </summary>
internal sealed class McpInteractionChannel : IReplInteractionChannel
{
	private readonly IReadOnlyDictionary<string, string> _prefillAnswers;
	private readonly InteractivityMode _mode;
	private readonly McpServer? _server;
	private readonly ProgressToken? _progressToken;
	private readonly IMcpFeedback? _feedback;

	public McpInteractionChannel(
		IReadOnlyDictionary<string, string> prefillAnswers,
		InteractivityMode mode,
		McpServer? server = null,
		ProgressToken? progressToken = null,
		IMcpFeedback? feedback = null)
	{
		_prefillAnswers = prefillAnswers;
		_mode = mode;
		_server = server;
		_progressToken = progressToken;
		_feedback = feedback;
	}

	public async ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ResolveChoiceIndex(prefill, choices);
		}

		if (await TryElicitChoiceAsync(name, prompt, choices).ConfigureAwait(false) is { } elicited)
		{
			return ResolveChoiceIndex(elicited, choices);
		}

		if (await TrySampleAsync($"{prompt}\nChoose one: {string.Join(", ", choices)}")
			.ConfigureAwait(false) is { } sampled)
		{
			return ResolveChoiceIndex(sampled, choices);
		}

		if (defaultIndex is { } idx)
		{
			return idx;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return 0;
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer.{name}'. Choices: {string.Join(", ", choices)}");
	}

	public async ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool? defaultValue = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ParseBool(prefill);
		}

		if (await TryElicitBoolAsync(name, prompt, defaultValue ?? false).ConfigureAwait(false) is { } elicited)
		{
			return elicited;
		}

		if (await TrySampleAsync($"{prompt} (yes/no)").ConfigureAwait(false) is { } sampled)
		{
			return ParseBool(sampled);
		}

		if (defaultValue is { } val)
		{
			return val;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return false;
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer.{name}' (true/false).");
	}

	public async ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return prefill;
		}

		if (await TryElicitTextAsync(name, prompt, defaultValue).ConfigureAwait(false) is { } elicited)
		{
			return elicited;
		}

		if (await TrySampleAsync(prompt).ConfigureAwait(false) is { } sampled)
		{
			return sampled;
		}

		if (defaultValue is not null)
		{
			return defaultValue;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return string.Empty;
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer.{name}'.");
	}

	public ValueTask<string> AskSecretAsync(
		string name,
		string prompt,
		AskSecretOptions? options = null)
	{
		// Secrets: prefill ONLY, never elicitation or sampling (security).
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ValueTask.FromResult(prefill);
		}

		throw new McpInteractionException(
			$"Secret prompt '{name}' requires a prefilled value. " +
			$"Provide it as a tool argument 'answer.{name}'.");
	}

	public async ValueTask<IReadOnlyList<int>> AskMultiChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		IReadOnlyList<int>? defaultIndices = null,
		AskMultiChoiceOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ParseMultiChoice(prefill, choices, options);
		}

		if (await TrySampleAsync($"{prompt}\nSelect from: {string.Join(", ", choices)}")
			.ConfigureAwait(false) is { } sampled)
		{
			return ParseMultiChoice(sampled, choices, options);
		}

		if (defaultIndices is { Count: > 0 })
		{
			return defaultIndices;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return [];
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer.{name}' (comma-separated). " +
			$"Choices: {string.Join(", ", choices)}");
	}

	public async ValueTask WriteProgressAsync(string label, double? percent, CancellationToken cancellationToken)
	{
		if (_feedback is not null)
		{
			await _feedback.ReportProgressAsync(
					new ReplProgressEvent(label, Percent: percent),
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (_server is not null && _progressToken is { } token)
		{
			await _server.NotifyProgressAsync(
				token,
				new ProgressNotificationValue
				{
					Progress = (float)(percent ?? 0),
					Total = 100f,
					Message = label,
				},
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}
	}

	public async ValueTask WriteStatusAsync(string text, CancellationToken cancellationToken)
	{
		await SendFeedbackAsync(
				LoggingLevel.Info,
				JsonSerializer.SerializeToElement(text, McpJsonContext.Default.String),
				cancellationToken)
			.ConfigureAwait(false);
	}

	public ValueTask ClearScreenAsync(CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	public ValueTask<TResult> DispatchAsync<TResult>(
		InteractionRequest<TResult> request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		return request switch
		{
			WriteStatusRequest status => CompleteBuiltInDispatchAsync<TResult>(
				SendFeedbackAsync(
					LoggingLevel.Info,
					JsonSerializer.SerializeToElement(status.Text, McpJsonContext.Default.String),
					cancellationToken)),
			WriteProgressRequest progress => CompleteBuiltInDispatchAsync<TResult>(
				WriteStructuredProgressAsync(progress, cancellationToken)),
			WriteNoticeRequest notice => CompleteBuiltInDispatchAsync<TResult>(
				SendFeedbackAsync(
					LoggingLevel.Info,
					JsonSerializer.SerializeToElement(notice.Text, McpJsonContext.Default.String),
					cancellationToken)),
			WriteWarningRequest warning => CompleteBuiltInDispatchAsync<TResult>(
				SendFeedbackAsync(
					LoggingLevel.Warning,
					JsonSerializer.SerializeToElement(warning.Text, McpJsonContext.Default.String),
					cancellationToken)),
			WriteProblemRequest problem => CompleteBuiltInDispatchAsync<TResult>(
				SendFeedbackAsync(
					LoggingLevel.Error,
					SerializeProblem(problem),
					cancellationToken)),
			_ => throw new NotSupportedException(
				$"No MCP interaction handler is registered for '{request.GetType().Name}'."),
		};
	}

	private async ValueTask SendFeedbackAsync(
		LoggingLevel level,
		JsonElement data,
		CancellationToken cancellationToken)
	{
		if (_feedback is not null)
		{
			await _feedback.SendMessageAsync(level, data, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (_server is null)
		{
			return;
		}

		await _server.SendNotificationAsync(
			NotificationMethods.LoggingMessageNotification,
			new LoggingMessageNotificationParams
			{
				Level = level,
				Logger = "repl.interaction",
				Data = data,
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask WriteStructuredProgressAsync(
		WriteProgressRequest progress,
		CancellationToken cancellationToken)
	{
		if (_feedback is not null)
		{
			await _feedback.ReportProgressAsync(
					new ReplProgressEvent(
						progress.Label,
						Percent: progress.Percent,
						State: progress.State,
						Details: progress.Details),
					cancellationToken)
				.ConfigureAwait(false);

			if (progress.State == ReplProgressState.Warning)
			{
				await _feedback.SendMessageAsync(
						LoggingLevel.Warning,
						BuildProgressPayload(progress),
						cancellationToken)
					.ConfigureAwait(false);
			}
			else if (progress.State == ReplProgressState.Error)
			{
				await _feedback.SendMessageAsync(
						LoggingLevel.Error,
						BuildProgressPayload(progress),
						cancellationToken)
					.ConfigureAwait(false);
			}

			return;
		}

		await WriteProgressAsync(progress.Label, progress.Percent, cancellationToken).ConfigureAwait(false);
	}

	private static JsonElement BuildProgressPayload(WriteProgressRequest progress)
	{
		var payload = new JsonObject
		{
			["label"] = progress.Label,
			["state"] = progress.State.ToString(),
		};

		if (progress.Percent is { } percent)
		{
			payload["percent"] = percent;
		}

		if (!string.IsNullOrWhiteSpace(progress.Details))
		{
			payload["details"] = progress.Details;
		}

		return JsonSerializer.SerializeToElement(payload, McpJsonContext.Default.JsonObject);
	}

	private static JsonElement SerializeProblem(WriteProblemRequest problem)
	{
		var payload = new JsonObject
		{
			["summary"] = problem.Summary,
		};

		if (!string.IsNullOrWhiteSpace(problem.Details))
		{
			payload["details"] = problem.Details;
		}

		if (!string.IsNullOrWhiteSpace(problem.Code))
		{
			payload["code"] = problem.Code;
		}

		return JsonSerializer.SerializeToElement(payload, McpJsonContext.Default.JsonObject);
	}

	private static async ValueTask<TResult> CompleteBuiltInDispatchAsync<TResult>(ValueTask operation)
	{
		await operation.ConfigureAwait(false);
		return (TResult)(object)true;
	}

	// ── Elicitation (Tier 2) ───────────────────────────────────────────

	private async Task<string?> TryElicitChoiceAsync(string name, string prompt, IReadOnlyList<string> choices)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
					{
						Enum = choices.ToList(),
					},
				},
			},
		}).ConfigureAwait(false);

		return ExtractElicitedString(result, name);
	}

	private async Task<bool?> TryElicitBoolAsync(string name, string prompt, bool defaultValue)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.BooleanSchema(),
				},
			},
		}).ConfigureAwait(false);

		if (result.IsAccepted && result.Content?.TryGetValue(name, out var value) is true)
		{
			return value.ValueKind == JsonValueKind.True;
		}

		return null;
	}

	private async Task<string?> TryElicitTextAsync(string name, string prompt, string? defaultValue)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.StringSchema(),
				},
			},
		}).ConfigureAwait(false);

		return ExtractElicitedString(result, name);
	}

	private bool CanElicit =>
		_mode is InteractivityMode.PrefillThenElicitation
		&& _server?.ClientCapabilities?.Elicitation is not null;

	private static string? ExtractElicitedString(ElicitResult result, string name)
	{
		if (!result.IsAccepted || result.Content?.TryGetValue(name, out var value) is not true)
		{
			return null;
		}

		return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
	}

	// ── Sampling (Tier 3) ──────────────────────────────────────────────

	private async Task<string?> TrySampleAsync(string prompt)
	{
		if (!CanSample)
		{
			return null;
		}

		var result = await _server!.SampleAsync(new CreateMessageRequestParams
		{
			Messages =
			[
				new SamplingMessage
				{
					Role = Role.User,
					Content = [new TextContentBlock { Text = prompt }],
				},
			],
			MaxTokens = 100,
		}).ConfigureAwait(false);

		return result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
	}

	private bool CanSample =>
		_mode is InteractivityMode.PrefillThenElicitation or InteractivityMode.PrefillThenSampling
		&& _server?.ClientCapabilities?.Sampling is not null;

	// ── Helpers ─────────────────────────────────────────────────────────

	/// <summary>
	/// Matches a choice value by exact name then unambiguous prefix — same logic as the
	/// console interaction channel's MatchChoiceByName.
	/// </summary>
	private static int ResolveChoiceIndex(string value, IReadOnlyList<string> choices)
	{
		// Exact match.
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], value, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		// Unambiguous prefix match.
		var prefixMatch = -1;
		for (var i = 0; i < choices.Count; i++)
		{
			if (choices[i].StartsWith(value, StringComparison.OrdinalIgnoreCase))
			{
				if (prefixMatch >= 0)
				{
					prefixMatch = -1;
					break; // Ambiguous — more than one match.
				}

				prefixMatch = i;
			}
		}

		if (prefixMatch >= 0)
		{
			return prefixMatch;
		}

		throw new McpInteractionException(
			$"Cannot resolve choice '{value}'. Available: {string.Join(", ", choices)}");
	}

	private static bool ParseBool(string value)
	{
		if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "y", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "1", StringComparison.Ordinal))
		{
			return true;
		}

		if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "n", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(value, "0", StringComparison.Ordinal))
		{
			return false;
		}

		throw new McpInteractionException(
			$"Cannot parse '{value}' as boolean. Use yes/no, true/false, y/n, or 1/0.");
	}

	private static int[] ParseMultiChoice(
		string value,
		IReadOnlyList<string> choices,
		AskMultiChoiceOptions? options = null)
	{
		var indices = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => ResolveChoiceIndex(token, choices))
			.ToArray();

		if (options?.MinSelections is { } min && indices.Length < min)
		{
			throw new McpInteractionException(
				$"At least {min} selection(s) required, but got {indices.Length}.");
		}

		if (options?.MaxSelections is { } max && indices.Length > max)
		{
			throw new McpInteractionException(
				$"At most {max} selection(s) allowed, but got {indices.Length}.");
		}

		return indices;
	}
}
