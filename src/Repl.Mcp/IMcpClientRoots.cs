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
	/// Native roots are preferred when supported; otherwise soft roots are returned.
	/// </summary>
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
