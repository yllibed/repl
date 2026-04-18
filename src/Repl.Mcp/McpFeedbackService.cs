using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpFeedback"/> backed by a live <see cref="McpServer"/> session.
/// </summary>
internal sealed class McpFeedbackService : IMcpFeedback
{
	private const string LoggerName = "repl.interaction";

	private readonly AsyncLocal<ProgressToken?> _progressToken = new();
	private McpServer? _server;

	public bool IsProgressSupported => _server is not null && _progressToken.Value is not null;

	public bool IsLoggingSupported => _server is not null;

	public async ValueTask ReportProgressAsync(
		ReplProgressEvent progress,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(progress);

		if (!IsProgressSupported || progress.State == ReplProgressState.Clear || _progressToken.Value is not { } progressToken)
		{
			return;
		}

		var percent = progress.ResolvePercent();
		await _server!.NotifyProgressAsync(
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
		LoggingLevel level,
		object? data,
		CancellationToken cancellationToken = default)
	{
		if (!IsLoggingSupported)
		{
			return;
		}

		await _server!.SendNotificationAsync(
			NotificationMethods.LoggingMessageNotification,
			new LoggingMessageNotificationParams
			{
				Level = level,
				Logger = LoggerName,
				Data = SerializeData(data),
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	internal void AttachServer(McpServer server) => _server = server;

	internal IDisposable PushProgressToken(ProgressToken? progressToken) =>
		new ProgressTokenScope(_progressToken, progressToken);

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
