namespace Repl.Mcp.AspNetCore;

internal sealed record McpHttpBinding(
	string Host,
	int Port,
	string Path,
	string ListenUrl,
	string EndpointUrl,
	bool AllowsRemote);
