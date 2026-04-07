namespace Repl.Mcp;

internal sealed record McpAppResourceRegistration(
	string Uri,
	Delegate Handler,
	McpAppResourceOptions Options);
