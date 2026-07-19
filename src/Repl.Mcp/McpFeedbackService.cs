using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005); the designated successor for server-initiated flows (SEP-2322,
// multi-round-trip requests, shipped experimentally in SDK 2.0 as MrtrContext/MrtrExchange)
// is not adopted by Repl yet, and hosts still rely on these features, so Repl keeps
// supporting them until the SDK removes the surface (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

/// <summary>
/// Internal implementation of <see cref="IMcpFeedback"/> backed by a live <see cref="McpServer"/> session.
/// </summary>
internal sealed class McpFeedbackService(McpRequestServerAccessor servers) : IMcpFeedback
{
	private const string LoggerName = "repl.interaction";

	private readonly AsyncLocal<ProgressToken?> _progressToken = new();

	public bool IsProgressSupported => servers.Effective is not null && _progressToken.Value is not null;

	public bool IsLoggingSupported => servers.Effective is not null;

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
		LoggingLevel level,
		object? data,
		CancellationToken cancellationToken = default)
	{
		if (servers.Effective is not { } server)
		{
			return;
		}

		await server.SendNotificationAsync(
			NotificationMethods.LoggingMessageNotification,
			new LoggingMessageNotificationParams
			{
				Level = level,
				Logger = LoggerName,
				Data = SerializeData(data),
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}


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
