using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005); the designated successor for server-initiated flows (SEP-2322,
// multi-round-trip requests) is not yet consumable in the SDK and hosts still rely on
// these features, so Repl keeps supporting them until the SDK removes the surface (#51).
#pragma warning disable MCP9005

namespace Repl.Mcp;

internal sealed class McpClientRootsService : IMcpClientRoots
{
	private readonly ICoreReplApp _app;
	private readonly McpRequestServerAccessor _servers;
	private readonly Lock _syncRoot = new();
	// Hard roots are SESSION state: one handler can serve several root-capable sessions,
	// and serving session A's cached roots to session B would expose A's workspace URIs
	// and build B's root-dependent snapshot from the wrong workspace. Entries are keyed by
	// the destination server (weak keys — they die with the session); a single global
	// version stamp invalidates every entry on any roots-list change (coarse, but the
	// event is rare and correctness beats granularity here).
	private readonly System.Runtime.CompilerServices.ConditionalWeakTable<McpServer, SessionRoots> _sessionRoots = [];
	private McpClientRoot[] _softRoots = [];
	private long _rootsVersion;

	public McpClientRootsService(ICoreReplApp app, McpRequestServerAccessor servers)
	{
		_app = app;
		_servers = servers;
	}

	private sealed class SessionRoots
	{
		public McpClientRoot[] Roots = [];
		public bool Loaded;
		public long LoadedVersion;
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
			var server = _servers.Effective;
			lock (_syncRoot)
			{
				if (server?.ClientCapabilities?.Roots is null)
				{
					return _softRoots;
				}

				var entry = _sessionRoots.GetOrCreateValue(server);
				return entry.Loaded && entry.LoadedVersion == _rootsVersion ? entry.Roots : [];
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

		var entry = _sessionRoots.GetOrCreateValue(server);
		long versionAtStart;
		lock (_syncRoot)
		{
			versionAtStart = _rootsVersion;
			if (entry.Loaded && entry.LoadedVersion == versionAtStart)
			{
				return entry.Roots;
			}
		}

		return await GetAndMaybeCacheRootsAsync(server, entry, versionAtStart, cancellationToken).ConfigureAwait(false);
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
			_rootsVersion++;
		}

		_app.InvalidateRouting();
	}

	private async ValueTask<IReadOnlyList<McpClientRoot>> GetAndMaybeCacheRootsAsync(
		McpServer server,
		SessionRoots entry,
		long versionAtStart,
		CancellationToken cancellationToken)
	{
		var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
			.ConfigureAwait(false);
		var mappedRoots = result.Roots?.Select(MapRoot).ToArray() ?? [];

		lock (_syncRoot)
		{
			if (_rootsVersion == versionAtStart)
			{
				entry.Roots = mappedRoots;
				entry.Loaded = true;
				entry.LoadedVersion = versionAtStart;
				return entry.Roots;
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
