using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Repl.Mcp.AspNetCore;

internal sealed class ReplMcpHttpSecurityMiddleware
{
	private static readonly Action<ILogger, string, Exception?> LogRejectedHost =
		LoggerMessage.Define<string>(
			LogLevel.Warning,
			new EventId(1, nameof(LogRejectedHost)),
			"Rejected MCP HTTP request with Host '{Host}'.");

	private static readonly Action<ILogger, string, Exception?> LogRejectedOrigin =
		LoggerMessage.Define<string>(
			LogLevel.Warning,
			new EventId(2, nameof(LogRejectedOrigin)),
			"Rejected MCP HTTP request with Origin '{Origin}'.");

	private readonly RequestDelegate _next;
	private readonly ILogger<ReplMcpHttpSecurityMiddleware> _logger;
	private readonly ReplMcpHttpSecurityOptions _options;

	public ReplMcpHttpSecurityMiddleware(
		RequestDelegate next,
		ILogger<ReplMcpHttpSecurityMiddleware> logger,
		ReplMcpHttpSecurityOptions options)
	{
		_next = next;
		_logger = logger;
		_options = options;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		if (!IsAllowedHost(context.Request.Host.Host))
		{
			ReplMcpHttpDiagnostics.RejectedRequests.Add(1, new KeyValuePair<string, object?>("reason", "host"));
			LogRejectedHost(_logger, context.Request.Host.Value ?? string.Empty, null);
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		if (!IsAllowedOrigin(context.Request.Headers.Origin.ToString()))
		{
			ReplMcpHttpDiagnostics.RejectedRequests.Add(1, new KeyValuePair<string, object?>("reason", "origin"));
			LogRejectedOrigin(_logger, context.Request.Headers.Origin.ToString(), null);
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		using var activity = ReplMcpHttpDiagnostics.ActivitySource.StartActivity(
			"Repl.Mcp.HttpRequest",
			ActivityKind.Server);
		await _next(context).ConfigureAwait(false);
	}

	private bool IsAllowedHost(string? host)
	{
		if (_options.AllowAnyHost)
		{
			return true;
		}

		if (string.IsNullOrWhiteSpace(host))
		{
			return false;
		}

		var normalized = NormalizeHost(host);
		return _options.AllowedHosts
			.Select(NormalizeHost)
			.Contains(normalized, StringComparer.OrdinalIgnoreCase);
	}

	private bool IsAllowedOrigin(string origin)
	{
		if (_options.AllowAnyOrigin || string.IsNullOrWhiteSpace(origin))
		{
			return true;
		}

		return _options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
	}

	private static string NormalizeHost(string host)
	{
		var normalized = host.Trim();
		if (normalized.Length > 1 && normalized[0] == '[')
		{
			var closingBracket = normalized.IndexOf(']', StringComparison.Ordinal);
			if (closingBracket >= 0)
			{
				return normalized[..(closingBracket + 1)];
			}
		}

		if (ContainsMultipleColons(normalized))
		{
			return normalized;
		}

		var colon = normalized.LastIndexOf(':');
		return colon > 0 ? normalized[..colon] : normalized;
	}

	private static bool ContainsMultipleColons(string value)
	{
		var found = false;
		foreach (var character in value)
		{
			if (character != ':')
			{
				continue;
			}

			if (found)
			{
				return true;
			}

			found = true;
		}

		return false;
	}
}
