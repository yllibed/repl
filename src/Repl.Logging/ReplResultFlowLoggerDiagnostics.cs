using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl;

internal sealed partial class ReplResultFlowLoggerDiagnostics(IServiceProvider services) : IReplResultFlowDiagnostics
{
	private const string LoggerCategory = "Repl.ResultFlow";

	public void OnDiagnostic(ReplResultFlowDiagnostic diagnostic)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
			?? NullLoggerFactory.Instance;
		var logger = loggerFactory.CreateLogger(LoggerCategory);

		switch (diagnostic.Kind)
		{
			case ReplResultFlowDiagnosticKind.PageFetchStarting:
				PageFetchStarting(logger, diagnostic.Cursor, diagnostic.PageSize);
				break;
			case ReplResultFlowDiagnosticKind.PageFetchSucceeded:
				PageFetchSucceeded(logger, diagnostic.Cursor, diagnostic.PageSize, diagnostic.ItemCount ?? 0);
				break;
			case ReplResultFlowDiagnosticKind.PageFetchFailed:
				PageFetchFailed(logger, diagnostic.Exception, diagnostic.Cursor, diagnostic.PageSize);
				break;
		}
	}

	[LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Result-flow page fetch starting. Cursor: {Cursor}; PageSize: {PageSize}.")]
	private static partial void PageFetchStarting(ILogger logger, string? cursor, int pageSize);

	[LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Result-flow page fetch succeeded. Cursor: {Cursor}; PageSize: {PageSize}; ItemCount: {ItemCount}.")]
	private static partial void PageFetchSucceeded(ILogger logger, string? cursor, int pageSize, int itemCount);

	[LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Result-flow page fetch failed. Cursor: {Cursor}; PageSize: {PageSize}.")]
	private static partial void PageFetchFailed(ILogger logger, Exception? exception, string? cursor, int pageSize);
}
