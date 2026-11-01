using System.Net;

namespace DocsMcpSample;

internal sealed record Settings(string Documents, string Index, string Assets, string Pipe, IPAddress AdminAddress, int McpPort, int AdminPort)
{
	internal string? AdminOrigin { get; init; }
	internal bool AllowsOrigin(string origin) => string.IsNullOrEmpty(origin) || new[]
	{
		$"http://localhost:{AdminPort}", $"http://127.0.0.1:{AdminPort}", $"http://[::1]:{AdminPort}", AdminOrigin,
	}.Contains(origin, StringComparer.OrdinalIgnoreCase);

	internal static Settings FromEnvironment() => new(
		Path.GetFullPath(Environment.GetEnvironmentVariable("DOCS_DOCUMENTS") ?? "documents"),
		Path.GetFullPath(Environment.GetEnvironmentVariable("DOCS_INDEX") ?? ".index"),
		Path.GetFullPath(Environment.GetEnvironmentVariable("DOCS_ASSETS") ?? ".assets"),
		Environment.GetEnvironmentVariable("DOCS_PIPE") ?? "repl-docs-index",
		IPAddress.Parse(Environment.GetEnvironmentVariable("DOCS_ADMIN_ADDRESS") ?? "127.0.0.1"),
		int.Parse(Environment.GetEnvironmentVariable("DOCS_MCP_PORT") ?? "8080", System.Globalization.CultureInfo.InvariantCulture),
		int.Parse(Environment.GetEnvironmentVariable("DOCS_ADMIN_PORT") ?? "8081", System.Globalization.CultureInfo.InvariantCulture))
		{ AdminOrigin = Environment.GetEnvironmentVariable("DOCS_ADMIN_ORIGIN") };

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
