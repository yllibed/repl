namespace Repl;

/// <summary>The input surface that submitted an invocation; independent of module-presence channels.</summary>
public enum ReplInvocationChannel
{
	/// <summary>Command-line input.</summary>
	Cli,
	/// <summary>Local interactive input.</summary>
	Interactive,
	/// <summary>Hosted interactive input.</summary>
	HostedInteractive,
	/// <summary>A public structured or textual request.</summary>
	Programmatic,
	/// <summary>An MCP tool or resource invocation.</summary>
	Mcp,
}
