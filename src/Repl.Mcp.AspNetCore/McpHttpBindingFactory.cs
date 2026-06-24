using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Repl.Mcp.AspNetCore;

internal static class McpHttpBindingFactory
{
	public static McpHttpBinding Create(
		string? host,
		int port,
		string? path,
		bool allowRemote)
	{
		if (port is < 1 or > 65535)
		{
			throw new InvalidOperationException("MCP HTTP port must be between 1 and 65535.");
		}

		var effectiveHost = string.IsNullOrWhiteSpace(host)
			? ReplMcpHttpServerOptions.DefaultHost
			: host.Trim();

		var isLoopback = IsLoopbackHost(effectiveHost);
		if (!allowRemote && !isLoopback)
		{
			throw new InvalidOperationException(
				"Remote MCP HTTP bindings require --allow-remote. Use 127.0.0.1 or localhost for local-only serving.");
		}

		var normalizedPath = NormalizePath(path);
		var urlHost = FormatUrlHost(effectiveHost);
		var listenUrl = string.Concat("http://", urlHost, ":", port.ToString(CultureInfo.InvariantCulture));
		var endpointUrl = string.Concat(listenUrl, normalizedPath);

		return new McpHttpBinding(
			effectiveHost,
			port,
			normalizedPath,
			listenUrl,
			endpointUrl,
			!isLoopback);
	}

	private static string NormalizePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return ReplMcpHttpServerOptions.DefaultPath;
		}

		var normalized = path.Trim();
		if (normalized[0] != '/')
		{
			normalized = "/" + normalized;
		}

		return normalized;
	}

	private static bool IsLoopbackHost(string host)
	{
		var normalized = TrimIpv6Brackets(host);
		if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return IPAddress.TryParse(normalized, out var address)
			&& IPAddress.IsLoopback(address);
	}

	private static string FormatUrlHost(string host)
	{
		var normalized = TrimIpv6Brackets(host);
		if (IPAddress.TryParse(normalized, out var address)
			&& address.AddressFamily == AddressFamily.InterNetworkV6)
		{
			return $"[{normalized}]";
		}

		return normalized;
	}

	private static string TrimIpv6Brackets(string host) =>
		host.Length > 1 && host[0] == '[' && host[^1] == ']'
			? host[1..^1]
			: host;
}
