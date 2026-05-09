using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl;

internal sealed partial class ReplResultFlowLoggerDiagnostics : IReplResultFlowDiagnostics
{
	private const string LoggerCategory = "Repl.ResultFlow";
	private readonly ILogger _logger;

	public ReplResultFlowLoggerDiagnostics(IServiceProvider services)
	{
		ArgumentNullException.ThrowIfNull(services);
		var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
			?? NullLoggerFactory.Instance;
		_logger = loggerFactory.CreateLogger(LoggerCategory);
	}

	public void OnDiagnostic(ReplResultFlowDiagnostic diagnostic)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);

		switch (diagnostic.Kind)
		{
			case ReplResultFlowDiagnosticKind.PageFetchStarting:
				PageFetchStarting(_logger, diagnostic.Cursor, diagnostic.PageSize);
				break;
			case ReplResultFlowDiagnosticKind.PageFetchSucceeded:
				PageFetchSucceeded(_logger, diagnostic.Cursor, diagnostic.PageSize, diagnostic.ItemCount ?? 0);
				break;
			case ReplResultFlowDiagnosticKind.PageFetchFailed:
				PageFetchFailed(_logger, diagnostic.Exception, diagnostic.Cursor, diagnostic.PageSize);
				break;
		}
	}

	[LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Result-flow page fetch starting. Cursor: {Cursor}; PageSize: {PageSize}.")]
	private static partial void PageFetchStarting(ILogger logger, string? cursor, int pageSize);

	[LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Result-flow page fetch succeeded. Cursor: {Cursor}; PageSize: {PageSize}; ItemCount: {ItemCount}.")]
	private static partial void PageFetchSucceeded(ILogger logger, string? cursor, int pageSize, int itemCount);

	[LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Result-flow page fetch failed. Cursor: {Cursor}; PageSize: {PageSize}.")]
	private static partial void PageFetchFailed(ILogger logger, Exception? exception, string? cursor, int pageSize);
}
