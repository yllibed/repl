using System.Net;
using Microsoft.Extensions.Configuration;

namespace DocsMcpSample;

internal sealed record Settings(string Documents, string Index, string Assets, string Pipe, IPAddress AdminAddress, int McpPort, int AdminPort)
{
	internal string? AdminOrigin { get; init; }
	internal IPAddress McpAddress { get; init; } = IPAddress.Loopback;

	internal static Settings FromConfiguration(IConfiguration configuration) => new(
		Path.GetFullPath(configuration["DOCUMENTS"] ?? "documents"),
		Path.GetFullPath(configuration["INDEX"] ?? ".index"),
		Path.GetFullPath(configuration["ASSETS"] ?? ".assets"),
		configuration["PIPE"] ?? "repl-docs-index",
		IPAddress.Parse(configuration["ADMIN_ADDRESS"] ?? "127.0.0.1"),
		configuration.GetValue("MCP_PORT", 8080),
		configuration.GetValue("ADMIN_PORT", 8081))
			{ AdminOrigin = configuration["ADMIN_ORIGIN"], McpAddress = IPAddress.Parse(configuration["MCP_ADDRESS"] ?? "127.0.0.1") };

	internal void Validate()
	{
		if (!Directory.Exists(Documents)) { throw new DirectoryNotFoundException($"Documents folder not found: {Documents}"); }
		if ((File.GetAttributes(Documents) & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("Use a documents directory directly, not a symbolic link."); }
		if (AdminOrigin is not null && (!Uri.TryCreate(AdminOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") || origin.GetLeftPart(UriPartial.Authority) != AdminOrigin))
		{
			throw new ArgumentException("DOCS_ADMIN_ORIGIN must be an HTTP(S) origin without a path or trailing slash.");
		}
		if (McpPort is < 1 or > 65535 || AdminPort is < 1 or > 65535 || McpPort == AdminPort)
		{
			throw new ArgumentException("MCP and administration require different ports between 1 and 65535.");
		}
		var relative = Path.GetRelativePath(Documents, Index);
		if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
		{
			throw new ArgumentException("Keep the index outside the documents folder.");
		}
		Directory.CreateDirectory(Index);
	}
}
