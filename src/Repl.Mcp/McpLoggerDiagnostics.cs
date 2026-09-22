using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl.Mcp;

/// <summary>
/// Operator-facing record of the failures this package absorbs on purpose. Each one is withheld from,
/// or hidden behind a fallback for, the remote MCP client by design; this is what keeps it from also
/// disappearing for the operator.
/// </summary>
/// <remarks>
/// Event ids 2000-2999 belong to this package; <c>Repl.Logging</c> uses the 1000 range. A failure that
/// can repeat on every request warns once per episode and logs its repeats at Debug, so a client that
/// keeps asking cannot flood the operator's log with the same exception.
/// </remarks>
internal sealed partial class McpLoggerDiagnostics
{
	private const string LoggerCategory = "Repl.Mcp";
	private readonly ILogger _logger;

	public McpLoggerDiagnostics(IServiceProvider services)
	{
		ArgumentNullException.ThrowIfNull(services);
		var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
			?? NullLoggerFactory.Instance;
		_logger = loggerFactory.CreateLogger(LoggerCategory);
	}

	[LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "An MCP command failed with exit code {ExitCode}; the client received a generic message instead of this detail: {Detail}")]
	public partial void ToolFailureWithheld(Exception? exception, int exitCode, string detail);

	[LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Building the MCP catalog at routing version {FailingVersion} failed; the connection keeps its previous catalog (version {ServedVersion}) until a rebuild succeeds or a visibility retraction withdraws it. Repeats are logged at Debug.")]
	public partial void StaleCatalogServed(Exception exception, long failingVersion, long servedVersion);

	[LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Priming the MCP client's roots failed; commands still run, and a handler that reads roots asks the client again. Repeats are logged at Debug until a prime succeeds.")]
	public partial void RootsPrimeFailed(Exception exception);

	[LoggerMessage(EventId = 2004, Level = LogLevel.Debug, Message = "Building the MCP catalog at routing version {FailingVersion} failed again; the previous catalog is still served.")]
	public partial void StaleCatalogStillServed(Exception exception, long failingVersion);

	[LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "The MCP catalog was rebuilt at routing version {Version} after a failure; the connection serves the current catalog again.")]
	public partial void CatalogRecovered(long version);

	[LoggerMessage(EventId = 2006, Level = LogLevel.Debug, Message = "Priming the MCP client's roots failed again.")]
	public partial void RootsPrimeStillFailing(Exception exception);
}
