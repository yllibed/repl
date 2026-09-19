using Repl.Interaction;
using Repl.Terminal;

namespace Repl.Mcp;

/// <summary>
/// The session-scoped services as <em>discovery</em> sees them on a modern revision: constants, holding
/// no reference to the live services, so the advertised command graph depends only on
/// application-global state.
/// </summary>
/// <remarks>
/// Revision <c>2026-07-28</c> conveys version, identity and capabilities as per-request metadata and
/// has no session, and its tools chapter requires that the advertised set
/// <em>"MUST NOT vary per-connection or as a side effect of other requests on the connection"</em> —
/// the one stated exception being the authorization presented on the request.
/// <para>
/// These wrappers therefore delegate <strong>nothing</strong>. Holding no inner service makes the
/// invariant structural rather than a property each member has to keep: whichever member a predicate
/// reaches for — <c>IsSupported</c>, <c>HasSoftRoots</c>, <c>Current</c>, <c>GetAsync</c> — there is
/// nothing per-connection behind it.
/// </para>
/// <para>
/// The capability questions answer "available" so that a gated command is advertised rather than
/// silently dropped, and the data questions answer "nothing", which is the only constant a list can
/// honestly take. The action members are inert: a presence predicate must not reach the client at all,
/// since doing so would be both a per-connection dependency and a side effect of discovery.
/// </para>
/// <para>
/// Execution binds handlers from the real services, so a command that needs roots and is called by a
/// client without them runs and can report exactly that, and soft roots set by a command remain fully
/// visible through <see cref="IMcpClientRoots.Current"/> there. Execution does take these answers for
/// deciding <em>presence</em>, so that what was advertised is what can be called; deciding it twice
/// from two views is what would make a tool visible and unreachable. Only the automatic revealing of
/// commands goes away.
/// </para>
/// <para>
/// The legacy revisions establish a session with an <c>initialize</c> handshake and state no such
/// invariant, so they keep the per-session view: these wrappers are applied on modern requests only.
/// </para>
/// </remarks>
internal static class McpDiscoveryCapabilities
{
	public static IMcpClientRoots Roots { get; } = new DiscoveryClientRoots();

	public static IMcpSampling Sampling { get; } = new DiscoverySampling();

	public static IMcpElicitation Elicitation { get; } = new DiscoveryElicitation();

	public static IMcpFeedback Feedback { get; } = new DiscoveryFeedback();

	/// <summary>
	/// Session state as discovery sees it: empty, and unchanged by anything written to it.
	/// </summary>
	/// <remarks>
	/// The capability services cover the per-connection half of the rule. This covers the other half,
	/// which needs no second connection to be observable: session state is a mutable singleton that a
	/// command can write, and a predicate reading it would let a <c>tools/call</c> decide what the next
	/// <c>tools/list</c> advertises — precisely the side effect the revision forbids. Reads answer
	/// "absent" because that is the only constant a store of arbitrary keys can honestly take; writes
	/// are inert, since a predicate must not mutate what it is measuring.
	/// </remarks>
	public static IReplSessionState SessionState { get; } = new DiscoverySessionState();

	/// <summary>
	/// Session metadata as discovery sees it: nothing known.
	/// </summary>
	/// <remarks>
	/// The live implementation is a façade over the ambient session, so its answers move with whichever
	/// connection happens to be resolving. A predicate gated on a terminal size or a transport name
	/// would therefore vary per connection, which is the first half of the rule.
	/// </remarks>
	public static IReplSessionInfo SessionInfo { get; } = new DiscoverySessionInfo();

	private sealed class DiscoveryClientRoots : IMcpClientRoots
	{
		public bool IsSupported => true;

		public bool HasSoftRoots => false;

		public IReadOnlyList<McpClientRoot> Current => [];

		public ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<McpClientRoot>>([]);

		public void SetSoftRoots(IEnumerable<McpClientRoot> roots)
		{
			// Inert: discovery must not mutate the state it is projecting.
		}

		public void ClearSoftRoots()
		{
			// Inert, for the same reason as SetSoftRoots.
		}
	}

	/// <summary>
	/// The frozen answers as a set, for the two places that must agree on them.
	/// </summary>
	/// <remarks>
	/// Discovery decides what is advertised; execution decides whether an advertised command exists.
	/// Those are the same question, and answering it twice from two different views is what makes a
	/// tool visible and uncallable. A fresh dictionary per call because the overlay owns what it is
	/// given.
	/// </remarks>
	public static Dictionary<Type, object> CreateSessionScopedOverrides() => new()
	{
		[typeof(IMcpClientRoots)] = Roots,
		[typeof(IMcpSampling)] = Sampling,
		[typeof(IMcpElicitation)] = Elicitation,
		[typeof(IMcpFeedback)] = Feedback,
		[typeof(IReplSessionState)] = SessionState,
		[typeof(IReplSessionInfo)] = SessionInfo,
	};

	private sealed class DiscoverySessionState : IReplSessionState
	{
		public bool TryGet<T>(string key, out T? value)
		{
			value = default;
			return false;
		}

		public T? Get<T>(string key) => default;

		public void Set<T>(string key, T value)
		{
			// Inert: a predicate must not mutate what it is measuring.
		}

		public bool Remove(string key) => false;

		public void Clear()
		{
			// Inert, for the same reason as Set.
		}
	}

	private sealed class DiscoverySessionInfo : IReplSessionInfo
	{
		public (int Width, int Height)? WindowSize => null;

		public bool AnsiSupported => false;

		public string? TransportName => null;

		public string? RemotePeer => null;

		public TerminalCapabilities TerminalCapabilities => TerminalCapabilities.None;

		public string? TerminalIdentity => null;

		public string? ShellIntegrationStatus => null;
	}

	private sealed class DiscoverySampling : IMcpSampling
	{
		public bool IsSupported => true;

		public ValueTask<string?> SampleAsync(
			string prompt,
			int maxTokens = 1024,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<string?>(null);
	}

	private sealed class DiscoveryElicitation : IMcpElicitation
	{
		public bool IsSupported => true;

		public ValueTask<string?> ElicitTextAsync(string message, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<string?>(null);

		public ValueTask<bool?> ElicitBooleanAsync(string message, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<bool?>(null);

		public ValueTask<int?> ElicitChoiceAsync(
			string message,
			IReadOnlyList<string> choices,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<int?>(null);

		public ValueTask<double?> ElicitNumberAsync(string message, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<double?>(null);
	}

	private sealed class DiscoveryFeedback : IMcpFeedback
	{
		public bool IsProgressSupported => true;

		public bool IsLoggingSupported => true;

		public ValueTask ReportProgressAsync(
			ReplProgressEvent progress,
			CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

		public ValueTask SendMessageAsync(
			McpMessageLevel level,
			object? data,
			CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
	}
}
