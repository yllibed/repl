namespace DocsMcpSample.Hosting;

internal static partial class Log
{
	[LoggerMessage(Level = LogLevel.Error, Message = "An unexpected administration request failure occurred.")]
	internal static partial void HttpFailure(ILogger logger, Exception error);
	[LoggerMessage(Level = LogLevel.Warning, Message = "Administration has no authentication. Keep port {Port} private; production requires access control and TLS.")]
	internal static partial void InsecureAdministration(ILogger logger, int port);
	[LoggerMessage(Level = LogLevel.Warning, Message = "Unauthenticated administration is listening on {Address}. Restrict access using the host network or a proxy.")]
	internal static partial void RemoteAdministration(ILogger logger, string address);
	[LoggerMessage(Level = LogLevel.Warning, Message = "Local administration connection failed.")]
	internal static partial void PipeFailure(ILogger logger, Exception error);
	[LoggerMessage(Level = LogLevel.Information, Message = "Preparing a replacement documentation index.")]
	internal static partial void IndexStarted(ILogger logger);
	[LoggerMessage(Level = LogLevel.Information, Message = "Published generation {Generation}: {Documents} documents and {Sections} sections.")]
	internal static partial void IndexPublished(ILogger logger, string generation, int documents, int sections);
	[LoggerMessage(Level = LogLevel.Information, Message = "Indexing cancelled; retaining the last published generation.")]
	internal static partial void IndexCancelled(ILogger logger);
	[LoggerMessage(Level = LogLevel.Error, Message = "Indexing failed; retaining the last published generation. Automatic retry at {RetryAt} unless the documents change or a manual rebuild is requested.")]
	internal static partial void IndexFailure(ILogger logger, Exception error, DateTimeOffset retryAt);
	[LoggerMessage(Level = LogLevel.Critical, Message = "Shutdown failed; terminating without disposing services beneath active work.")]
	internal static partial void ShutdownFailure(ILogger logger, Exception error);
}
