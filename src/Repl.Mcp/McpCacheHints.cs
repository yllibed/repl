using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

/// <summary>
/// Cache hints Repl attaches to results whose content depends on which client asked.
/// </summary>
internal static class McpCacheHints
{
	/// <summary>
	/// Marks <paramref name="result"/> as belonging to the requesting client alone, and as immediately
	/// stale — on the revisions that have somewhere to put that.
	/// </summary>
	/// <remarks>
	/// Set rather than left to a default, because the default is the wrong one: SEP-2549 reads an
	/// absent <c>cacheScope</c> as <c>Public</c>, and everything this package returns can vary by
	/// client — a command graph gated on that client's roots, a resource body produced by running a
	/// command for it. A shared gateway is entitled to serve a <c>Public</c> result to the next caller.
	/// Applied at the primitives as well as the handler, because a server built from
	/// <c>BuildMcpServerOptions()</c> dispatches straight into the primitives and never reaches a
	/// handler.
	/// <para>
	/// Both fields arrived with <c>2026-07-28</c> and are absent from the initialize-era result schema,
	/// so an older client is left untagged. It cannot be reached through a cache that understands these
	/// hints anyway, and a strict implementation may reject a response carrying a field its schema does
	/// not define — on the compatibility path this package exists to keep working.
	/// </para>
	/// </remarks>
	public static TResult MarkPrivateToThisClient<TResult>(MessageContext request, TResult result)
		where TResult : ICacheableResult
	{
		var protocolVersion = (request.JsonRpcMessage as JsonRpcRequest)?.Context?.ProtocolVersion;
		if (!McpProtocolRevisions.CarriesSessionlessFields(protocolVersion))
		{
			return result;
		}

		result.CacheScope = CacheScope.Private;
		result.TimeToLive = TimeSpan.Zero;
		return result;
	}
}
