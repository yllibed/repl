using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;

// Deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005); kept for existing hosts.
// Rationale and successor: docs/mcp-reference.md#sdk-and-protocol-versions (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpFeedback"/> backed by the flowing MCP request.
/// </summary>
internal sealed class McpFeedbackService(McpRequestServerAccessor servers) : IMcpFeedback
{
	private const string LoggerName = "repl.interaction";

	private readonly AsyncLocal<ProgressToken?> _progressToken = new();
	private readonly AsyncLocal<UndeliveredMessages?> _undelivered = new();

	public bool IsProgressSupported => servers.Effective is not null && _progressToken.Value is not null;

	public bool IsLoggingSupported => ResolveThreshold() is not null;

	public async ValueTask ReportProgressAsync(
		ReplProgressEvent progress,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(progress);

		// Single read: the effective server must not change between the support check and
		// the send (a concurrent request re-binding the accessor must not be observed).
		if (servers.Effective is not { } server
			|| progress.State == ReplProgressState.Clear
			|| _progressToken.Value is not { } progressToken)
		{
			return;
		}

		var percent = progress.ResolvePercent();
		await server.NotifyProgressAsync(
			progressToken,
			new ProgressNotificationValue
			{
				Progress = (float)Math.Clamp(percent ?? 0d, 0d, 100d),
				Total = progress.State == ReplProgressState.Indeterminate || percent is null ? null : 100f,
				Message = BuildProgressMessage(progress),
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SendMessageAsync(
		McpMessageLevel level,
		object? data,
		CancellationToken cancellationToken = default)
	{
		// Single read of both the server and the threshold: a concurrent request re-binding the
		// accessor must not be observed between the decision and the send.
		var server = servers.Effective;
		var threshold = ResolveThreshold();

		if (server is null || threshold is null || level < threshold)
		{
			// Either the client cannot receive notifications for this request, or the message is
			// below the level it asked for. Below-threshold messages are dropped as requested;
			// undeliverable ones are kept so the tool result can carry them instead.
			if (threshold is null)
			{
				_undelivered.Value?.Add(level, data);
			}

			return;
		}

		await server.SendNotificationAsync(
			NotificationMethods.LoggingMessageNotification,
			new LoggingMessageNotificationParams
			{
				Level = (LoggingLevel)level,
				Logger = LoggerName,
				Data = SerializeData(data),
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Resolves the severity threshold the current request asked for, or <see langword="null"/> when
	/// it asked for nothing and no notification may be sent.
	/// </summary>
	/// <remarks>
	/// The <c>2026-07-28</c> revision (SEP-2575) replaced <c>logging/setLevel</c> with a per-request
	/// <c>_meta/io.modelcontextprotocol/logLevel</c> field and states that a server MUST NOT emit
	/// message notifications for a request that omitted it. The SDK parses the field onto the message
	/// context but consumes it nowhere, so the filtering is Repl's to do.
	/// <para>
	/// Initialize-era revisions keep the session-wide <c>logging/setLevel</c> semantics, including the
	/// historical behaviour of sending everything when the client never set a level — those hosts must
	/// not lose feedback to a rule that does not apply to them.
	/// </para>
	/// <para>
	/// Note that the SDK's own CLIENT cannot currently ask for notifications on <c>2026-07-28</c>: it
	/// rejects <c>logging/setLevel</c> on that revision, exposes no option for the level, and replaces
	/// a caller's <c>_meta</c> with its own three keys (protocol version, client info, capabilities).
	/// So in practice this returns <see langword="null"/> for every SDK-client request on the modern
	/// revision, which is exactly why undeliverable messages are carried back in the tool result.
	/// </para>
	/// </remarks>
	private McpMessageLevel? ResolveThreshold()
	{
		if (servers.Current is not { } request)
		{
			return null;
		}

		var context = (request.JsonRpcMessage as JsonRpcRequest)?.Context;
		if (context?.LogLevel is { } requestedLevel)
		{
			return (McpMessageLevel)requestedLevel;
		}

		if (IsSessionlessRevision(context?.ProtocolVersion))
		{
			return null;
		}

		return request.Server.LoggingLevel is { } sessionLevel
			? (McpMessageLevel)sessionLevel
			: McpMessageLevel.Debug;
	}

	private static bool IsSessionlessRevision(string? protocolVersion) =>
		string.Equals(protocolVersion, McpProtocolRevisions.Sessionless, StringComparison.Ordinal);

	internal IDisposable PushProgressToken(ProgressToken? progressToken) =>
		new ProgressTokenScope(_progressToken, progressToken);

	/// <summary>
	/// Opens a scope that collects messages the current request cannot receive as notifications.
	/// </summary>
	/// <remarks>
	/// Pushed per invocation by <see cref="McpToolAdapter"/> and drained into the tool result, so a
	/// client that never asked for log notifications still sees what a command reported. The buffer
	/// is <see cref="AsyncLocal{T}"/> rather than a field on <see cref="McpInteractionChannel"/>
	/// because a command can inject <see cref="IMcpFeedback"/> directly and bypass the channel.
	/// </remarks>
	internal UndeliveredMessageScope PushUndeliveredMessages() => new(_undelivered);

	private static JsonElement SerializeData(object? data) =>
		data switch
		{
			null => JsonSerializer.SerializeToElement((string?)null, McpJsonContext.Default.String),
			string text => JsonSerializer.SerializeToElement(text, McpJsonContext.Default.String),
			bool value => JsonSerializer.SerializeToElement(value, McpJsonContext.Default.Boolean),
			JsonElement element => element,
			JsonObject value => JsonSerializer.SerializeToElement(value, McpJsonContext.Default.JsonObject),
			_ => JsonSerializer.SerializeToElement(data.ToString(), McpJsonContext.Default.String),
		};

	private static string BuildProgressMessage(ReplProgressEvent progress) =>
		string.IsNullOrWhiteSpace(progress.Details)
			? progress.Label
			: $"{progress.Label}: {progress.Details}";

	/// <summary>Messages collected for a request that cannot receive notifications.</summary>
	internal sealed class UndeliveredMessages
	{
		private readonly List<string> _lines = [];

		public void Add(McpMessageLevel level, object? data)
		{
			var text = data as string ?? data?.ToString();
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}

			lock (_lines)
			{
				_lines.Add($"[{level.ToString().ToLowerInvariant()}] {text}");
			}
		}

		public IReadOnlyList<string> Drain()
		{
			lock (_lines)
			{
				if (_lines.Count == 0)
				{
					return [];
				}

				var drained = _lines.ToArray();
				_lines.Clear();
				return drained;
			}
		}
	}

	/// <summary>Restores the previous buffer on dispose, so nested invocations stay independent.</summary>
	internal sealed class UndeliveredMessageScope : IDisposable
	{
		private readonly AsyncLocal<UndeliveredMessages?> _slot;
		private readonly UndeliveredMessages? _previous;
		private bool _disposed;

		public UndeliveredMessageScope(AsyncLocal<UndeliveredMessages?> slot)
		{
			_slot = slot;
			_previous = slot.Value;
			Messages = new UndeliveredMessages();
			slot.Value = Messages;
		}

		public UndeliveredMessages Messages { get; }

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_slot.Value = _previous;
			_disposed = true;
		}
	}

	private sealed class ProgressTokenScope : IDisposable
	{
		private readonly AsyncLocal<ProgressToken?> _progressTokenSlot;
		private readonly ProgressToken? _previousToken;
		private bool _disposed;

		public ProgressTokenScope(
			AsyncLocal<ProgressToken?> progressTokenSlot,
			ProgressToken? progressToken)
		{
			_progressTokenSlot = progressTokenSlot;
			_previousToken = progressTokenSlot.Value;
			_progressTokenSlot.Value = progressToken;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_progressTokenSlot.Value = _previousToken;
			_disposed = true;
		}
	}
}
