using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl;

/// <summary>
/// Adds ambient REPL execution metadata to the active <see cref="ILogger"/> scope.
/// </summary>
public static class ReplLoggingMiddleware
{
	private const string LoggerCategory = "Repl.Execution";

	/// <summary>
	/// Wraps command execution in a logging scope populated from the current REPL context.
	/// </summary>
	public static async ValueTask InvokeAsync(ReplExecutionContext context, ReplNext next)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(next);

		var loggerFactory = context.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
			?? NullLoggerFactory.Instance;
		var accessor = context.Services.GetService(typeof(IReplLogContextAccessor)) as IReplLogContextAccessor;

		if (accessor is null)
		{
			await next().ConfigureAwait(false);
			return;
		}

		var logContext = accessor.Current;
		var logger = loggerFactory.CreateLogger(LoggerCategory);
		using var scope = logger.BeginScope(CreateScope(logContext));
		await next().ConfigureAwait(false);
	}

	private static IReadOnlyList<KeyValuePair<string, object?>> CreateScope(ReplLogContext context) =>
	[
		new("ReplSessionId", context.SessionId),
		new("ReplSessionActive", context.IsSessionActive),
		new("ReplHostedSession", context.IsHostedSession),
		new("ReplProgrammatic", context.IsProgrammatic),
		new("ReplProtocolPassthrough", context.IsProtocolPassthrough),
		new("ReplTransport", context.TransportName),
		new("ReplRemotePeer", context.RemotePeer),
		new("ReplTerminalIdentity", context.TerminalIdentity),
	];
}
