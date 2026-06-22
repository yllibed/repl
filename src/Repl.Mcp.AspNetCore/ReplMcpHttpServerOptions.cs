using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Repl.Mcp.AspNetCore;

/// <summary>
/// Configures the self-hosted Repl MCP Streamable HTTP server.
/// </summary>
public sealed class ReplMcpHttpServerOptions
{
	/// <summary>
	/// Default local-only host.
	/// </summary>
	public static readonly string DefaultHost = "127.0.0.1";

	/// <summary>
	/// Default HTTP port. The digits correspond to "repl" on a phone keypad.
	/// </summary>
	public static readonly int DefaultPort = 7375;

	/// <summary>
	/// Default Streamable HTTP endpoint path.
	/// </summary>
	public static readonly string DefaultPath = "/mcp";

	/// <summary>
	/// Gets or sets the hostname or IP address to bind.
	/// </summary>
	public string Host { get; set; } = DefaultHost;

	/// <summary>
	/// Gets or sets the HTTP port to bind.
	/// </summary>
	public int Port { get; set; } = DefaultPort;

	/// <summary>
	/// Gets or sets the Streamable HTTP endpoint path.
	/// </summary>
	public string Path { get; set; } = DefaultPath;

	/// <summary>
	/// Gets or sets a value indicating whether non-loopback bindings are allowed.
	/// </summary>
	public bool AllowRemote { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether startup messages should be suppressed.
	/// </summary>
	public bool Quiet { get; set; }

	/// <summary>
	/// Gets the shared Repl MCP HTTP transport options.
	/// </summary>
	public ReplMcpHttpOptions Http { get; } = new();

	/// <summary>
	/// Gets the self-hosted HTTP security options.
	/// </summary>
	public ReplMcpHttpSecurityOptions Security { get; } = new();

	/// <summary>
	/// Gets or sets a callback invoked before the inner <see cref="WebApplication"/> is built.
	/// </summary>
	public Action<WebApplicationBuilder>? ConfigureBuilder { get; set; }

	/// <summary>
	/// Gets or sets a callback invoked after the inner <see cref="WebApplication"/> is built and before MCP is mapped.
	/// </summary>
	public Action<WebApplication>? ConfigureApp { get; set; }

	/// <summary>
	/// Gets or sets a callback invoked after the MCP endpoint is mapped.
	/// </summary>
	public Action<IEndpointConventionBuilder>? ConfigureEndpoint { get; set; }

	internal ReplMcpHttpServerOptions Clone()
	{
		var clone = new ReplMcpHttpServerOptions
		{
			Host = Host,
			Port = Port,
			Path = Path,
			AllowRemote = AllowRemote,
			Quiet = Quiet,
			ConfigureBuilder = ConfigureBuilder,
			ConfigureApp = ConfigureApp,
			ConfigureEndpoint = ConfigureEndpoint,
		};

		CopyNestedOptionsTo(clone);
		return clone;
	}

	internal void CopyNestedOptionsTo(ReplMcpHttpServerOptions target)
	{
		CopyHttpOptions(Http, target.Http);
		CopySecurityOptions(Security, target.Security);
	}

	private static void CopyHttpOptions(ReplMcpHttpOptions source, ReplMcpHttpOptions target)
	{
		target.ConfigureServer = source.ConfigureServer;
		target.ConfigureTransport = source.ConfigureTransport;
		target.EnableAuthorizationFilters = source.EnableAuthorizationFilters;
		target.Stateless = source.Stateless;
		target.PerSessionExecutionContext = source.PerSessionExecutionContext;
		target.IdleTimeout = source.IdleTimeout;
		target.MaxIdleSessionCount = source.MaxIdleSessionCount;
	}

	private static void CopySecurityOptions(ReplMcpHttpSecurityOptions source, ReplMcpHttpSecurityOptions target)
	{
		target.AllowAnyHost = source.AllowAnyHost;
		target.AllowAnyOrigin = source.AllowAnyOrigin;
		target.AllowedHosts.Clear();
		foreach (var host in source.AllowedHosts)
		{
			target.AllowedHosts.Add(host);
		}

		target.AllowedOrigins.Clear();
		foreach (var origin in source.AllowedOrigins)
		{
			target.AllowedOrigins.Add(origin);
		}
	}
}
