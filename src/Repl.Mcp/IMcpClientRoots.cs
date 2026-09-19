namespace Repl.Mcp;

/// <summary>
/// Provides access to MCP client roots for the current MCP session.
/// </summary>
public interface IMcpClientRoots
{
	/// <summary>
	/// Gets a value that indicates whether the connected MCP client supports native roots discovery.
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Gets a value that indicates whether soft roots were configured for the current session.
	/// </summary>
	bool HasSoftRoots { get; }

	/// <summary>
	/// Gets the current effective roots for the session.
	/// Native roots are preferred once resolved; otherwise soft roots are returned.
	/// </summary>
	/// <remarks>
	/// Under <c>mcp serve</c>, where this state belongs to the connection, a client that supports native
	/// roots but has not been asked yet or could not be reached leaves nothing resolved, and soft roots
	/// stand in for that — so an empty result means the roots in force are empty, not that resolving them
	/// failed. On a reused <c>BuildMcpServerOptions()</c> result the state belongs to the request instead,
	/// and a roots-capable client reads empty until <see cref="GetAsync"/> has been called within that
	/// request; soft roots answer only when the client supports no native roots at all. Either way, call
	/// <see cref="GetAsync"/> when the difference matters: it resolves on demand and surfaces a failure
	/// instead of absorbing it.
	/// </remarks>
	IReadOnlyList<McpClientRoot> Current { get; }

	/// <summary>
	/// Resolves the current effective roots for the session, refreshing native roots on demand when supported.
	/// </summary>
	ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Sets soft roots for the current session.
	/// </summary>
	void SetSoftRoots(IEnumerable<McpClientRoot> roots);

	/// <summary>
	/// Clears the soft roots for the current session.
	/// </summary>
	void ClearSoftRoots();
}
