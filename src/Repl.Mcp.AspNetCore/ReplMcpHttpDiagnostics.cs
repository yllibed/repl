using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Repl.Mcp.AspNetCore;

internal static class ReplMcpHttpDiagnostics
{
	public const string MeterName = "Repl.Mcp.Http";
	public const string ActivitySourceName = "Repl.Mcp.Http";

	public static readonly Meter Meter = new(MeterName);
	public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
	public static readonly Counter<long> RejectedRequests = Meter.CreateCounter<long>("repl.mcp.http.requests.rejected");
	public static readonly Counter<long> StartupFailures = Meter.CreateCounter<long>("repl.mcp.http.startup.failures");
}
