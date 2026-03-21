using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

internal sealed class McpClientRootsService : IMcpClientRoots
{
	private readonly ICoreReplApp _app;
	private readonly Lock _syncRoot = new();
	private McpServer? _server;
	private McpClientRoot[] _hardRoots = [];
	private McpClientRoot[] _softRoots = [];
	private bool _hardRootsLoaded;

	public McpClientRootsService(ICoreReplApp app)
	{
		_app = app;
	}

	public bool IsSupported => _server?.ClientCapabilities?.Roots is not null;

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

	public void AttachServer(McpServer server)
	{
		ArgumentNullException.ThrowIfNull(server);
		_server = server;
	}

	public async ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default)
	{
		var server = _server;
		if (server?.ClientCapabilities?.Roots is null)
		{
			return Current;
		}

		lock (_syncRoot)
		{
			if (_hardRootsLoaded)
			{
				return _hardRoots;
			}
		}

		var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
			.ConfigureAwait(false);
		var mappedRoots = result.Roots?.Select(MapRoot).ToArray() ?? [];

		lock (_syncRoot)
		{
			_hardRoots = mappedRoots;
			_hardRootsLoaded = true;
			return _hardRoots;
		}
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
		}

		_app.InvalidateRouting();
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
