namespace DocsMcpSample.Hosting;

internal static partial class Log
{
	[LoggerMessage(Level = LogLevel.Warning, Message = "Administration has no authentication. Keep port {Port} private; production requires access control and TLS.")]
	internal static partial void InsecureAdministration(ILogger logger, int port);
	[LoggerMessage(Level = LogLevel.Warning, Message = "Unauthenticated administration is listening on {Address}. Restrict access using the host network or a proxy.")]
	internal static partial void RemoteAdministration(ILogger logger, string address);
	[LoggerMessage(Level = LogLevel.Warning, Message = "Local administration connection failed.")]
	internal static partial void PipeFailure(ILogger logger, Exception error);
	[LoggerMessage(Level = LogLevel.Error, Message = "Indexing failed; retaining the last published generation.")]
	internal static partial void IndexFailure(ILogger logger, Exception error);
	[LoggerMessage(Level = LogLevel.Critical, Message = "Shutdown failed; terminating without disposing services beneath active work.")]
	internal static partial void ShutdownFailure(ILogger logger, Exception error);
}
