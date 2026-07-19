using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005); the designated successor for server-initiated flows (SEP-2322,
// multi-round-trip requests, shipped experimentally in SDK 2.0 as MrtrContext/MrtrExchange)
// is not adopted by Repl yet, and hosts still rely on these features, so Repl keeps
// supporting them until the SDK removes the surface (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

// One instance PER SESSION (owned by McpSessionContext): hard roots, soft roots, and
// their cache/version state are session state — one handler can serve several sessions,
// and serving session A's roots to session B would expose A's workspace URIs and build
// B's root-dependent snapshot from the wrong workspace. Outbound transport still goes
// through the request-bound accessor (the destination is per request, finer than the
// session).
internal sealed class McpClientRootsService : IMcpClientRoots
{
	private readonly ICoreReplApp _app;
	private readonly McpRequestServerAccessor _servers;
	private readonly Lock _syncRoot = new();
	private McpClientRoot[] _hardRoots = [];
	private McpClientRoot[] _softRoots = [];
	private bool _hardRootsLoaded;
	private long _hardRootsVersion;

	public McpClientRootsService(ICoreReplApp app, McpRequestServerAccessor servers)
	{
		_app = app;
		_servers = servers;
	}

	public bool IsSupported => _servers.Effective?.ClientCapabilities?.Roots is not null;

	public bool HasSoftRoots
	{
		get
		{
			lock (_syncRoot)
			{
				return _softRoots.Length > 0;
			}
		}
	}

	public IReadOnlyList<McpClientRoot> Current
	{
		get
		{
			lock (_syncRoot)
			{
				return IsSupported ? _hardRoots : _softRoots;
			}
		}
	}

	public async ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default)
	{
		// Single read: the effective server must not change between the support check and
		// the roots request (a concurrent request re-binding the accessor must not be observed).
		var server = _servers.Effective;
		if (server?.ClientCapabilities?.Roots is null)
		{
			return Current;
		}

		long versionAtStart;
		lock (_syncRoot)
		{
			if (_hardRootsLoaded)
			{
				return _hardRoots;
			}

			versionAtStart = _hardRootsVersion;
		}

		return await GetAndMaybeCacheRootsAsync(server, versionAtStart, cancellationToken).ConfigureAwait(false);
	}

	public void SetSoftRoots(IEnumerable<McpClientRoot> roots)
	{
		ArgumentNullException.ThrowIfNull(roots);

		var normalized = roots.ToArray();
		var changed = false;
		lock (_syncRoot)
		{
			if (!AreEqual(_softRoots, normalized))
			{
				_softRoots = normalized;
				changed = true;
			}
		}

		if (changed)
		{
			_app.InvalidateRouting();
		}
	}

	public void ClearSoftRoots()
	{
		var changed = false;
		lock (_syncRoot)
		{
			if (_softRoots.Length > 0)
			{
				_softRoots = [];
				changed = true;
			}
		}

		if (changed)
		{
			_app.InvalidateRouting();
		}
	}

	public void HandleRootsListChanged()
	{
		lock (_syncRoot)
		{
			_hardRoots = [];
			_hardRootsLoaded = false;
			_hardRootsVersion++;
		}

		_app.InvalidateRouting();
	}

	private async ValueTask<IReadOnlyList<McpClientRoot>> GetAndMaybeCacheRootsAsync(
		McpServer server,
		long versionAtStart,
		CancellationToken cancellationToken)
	{
		var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
			.ConfigureAwait(false);
		var mappedRoots = result.Roots?.Select(MapRoot).ToArray() ?? [];

		lock (_syncRoot)
		{
			if (_hardRootsVersion == versionAtStart)
			{
				_hardRoots = mappedRoots;
				_hardRootsLoaded = true;
				return _hardRoots;
			}

			return mappedRoots;
		}
	}

	private static McpClientRoot MapRoot(Root root)
	{
		var uri = Uri.TryCreate(root.Uri, UriKind.Absolute, out var parsed)
			? parsed
			: new Uri(root.Uri, UriKind.RelativeOrAbsolute);
		return new McpClientRoot(uri, root.Name);
	}

	private static bool AreEqual(McpClientRoot[] left, McpClientRoot[] right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}

		if (left.Length != right.Length)
		{
			return false;
		}

		for (var i = 0; i < left.Length; i++)
		{
			if (left[i] != right[i])
			{
				return false;
			}
		}

		return true;
	}
}
