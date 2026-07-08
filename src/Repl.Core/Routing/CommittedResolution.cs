namespace Repl;

/// <summary>
/// The single resolution of one committed input line: whether it is an ambient command, a
/// help request, an ambiguous prefix, or a resolved route — captured against one
/// routing-graph snapshot and reused by both the shell-integration mark decision and
/// dispatch. Built through the per-kind factories so which fields are populated is
/// structural: the guarded accessors throw on a kind mismatch instead of null-forgiving
/// reads.
/// </summary>
internal readonly struct CommittedResolution
{
	private readonly GlobalInvocationOptions? _options;
	private readonly ActiveRoutingGraph? _graph;
	private readonly PrefixResolutionResult? _prefix;
	private readonly RouteResolver.RouteResolutionResult? _routes;

	private CommittedResolution(
		CommittedKind kind,
		GlobalInvocationOptions? options,
		ActiveRoutingGraph? graph,
		PrefixResolutionResult? prefix,
		RouteResolver.RouteResolutionResult? routes)
	{
		Kind = kind;
		_options = options;
		_graph = graph;
		_prefix = prefix;
		_routes = routes;
	}

	public static CommittedResolution Ambient() =>
		new(CommittedKind.Ambient, options: null, graph: null, prefix: null, routes: null);

	public static CommittedResolution Ambiguous(
		GlobalInvocationOptions options,
		ActiveRoutingGraph graph,
		PrefixResolutionResult prefix) =>
		new(CommittedKind.Ambiguous, options, graph, prefix, routes: null);

	public static CommittedResolution Help(
		GlobalInvocationOptions options,
		ActiveRoutingGraph graph,
		PrefixResolutionResult prefix) =>
		new(CommittedKind.Help, options, graph, prefix, routes: null);

	public static CommittedResolution Routed(
		GlobalInvocationOptions options,
		ActiveRoutingGraph graph,
		PrefixResolutionResult prefix,
		RouteResolver.RouteResolutionResult routes) =>
		new(CommittedKind.Routed, options, graph, prefix, routes);

	public CommittedKind Kind { get; }

	public GlobalInvocationOptions Options =>
		_options ?? throw new InvalidOperationException("An ambient resolution captures no global options.");

	public ActiveRoutingGraph Graph =>
		_graph ?? throw new InvalidOperationException("An ambient resolution captures no routing graph.");

	public PrefixResolutionResult Prefix =>
		_prefix ?? throw new InvalidOperationException("An ambient resolution captures no prefix result.");

	public RouteResolver.RouteResolutionResult Routes =>
		_routes ?? throw new InvalidOperationException($"A {Kind} resolution captures no route match.");

	public bool IsProtocolPassthrough =>
		Kind == CommittedKind.Routed && _routes?.Match?.Route.Command.IsProtocolPassthrough == true;
}
