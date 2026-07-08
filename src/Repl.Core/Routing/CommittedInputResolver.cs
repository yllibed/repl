namespace Repl;

/// <summary>
/// Resolves one committed interactive input line against a single
/// <see cref="CoreReplApp.ResolveActiveRoutingGraph"/> snapshot — prefix expansion, help
/// scoping, and the route match all use that one snapshot, so the passthrough
/// classification and the eventual execution can never diverge. Routing-resolution
/// concern extracted from the interactive loop; ambient classification stays with the
/// loop's dispatch table and is injected as a predicate.
/// </summary>
internal sealed class CommittedInputResolver(
	CoreReplApp app,
	Func<IReadOnlyList<string>, bool> isAmbientCommand)
{
	public CommittedResolution Resolve(IReadOnlyList<string> inputTokens, IReadOnlyList<string> scopeTokens)
	{
		if (isAmbientCommand(inputTokens))
		{
			// Ambient commands win over routes sharing the same token and produce
			// normal terminal output, never a protocol payload.
			return CommittedResolution.Ambient();
		}

		var invocationTokens = scopeTokens.Concat(inputTokens).ToArray();
		var globalOptions = GlobalOptionParser.Parse(invocationTokens, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);

		// Apply the parsed globals to the snapshot BEFORE resolving routes: module-presence
		// predicates read IGlobalOptionsAccessor during ResolveActiveRoutingGraph, so a
		// per-command global (e.g. `secret --env prod`) must be visible to routing or a
		// gated command looks missing / a passthrough route is misclassified.
		app.GlobalOptionsSnapshotInstance.Update(globalOptions.CustomGlobalNamedOptions);
		var graph = app.ResolveActiveRoutingGraph();

		// Resolve prefixes against the captured graph BEFORE deciding help or matching, so
		// an abbreviation (`ser` -> `server`) is expanded consistently and `ser --help`
		// scopes help to the resolved command — matching the non-interactive path.
		var prefixResolution = app.ResolveUniquePrefixes(globalOptions.RemainingTokens, graph);
		if (prefixResolution.IsAmbiguous)
		{
			return CommittedResolution.Ambiguous(globalOptions, graph, prefixResolution);
		}

		var resolvedOptions = globalOptions with { RemainingTokens = prefixResolution.Tokens };
		if (resolvedOptions.HelpRequested)
		{
			return CommittedResolution.Help(resolvedOptions, graph, prefixResolution);
		}

		var routes = app.ResolveWithDiagnostics(resolvedOptions.RemainingTokens, graph.Routes);
		return CommittedResolution.Routed(resolvedOptions, graph, prefixResolution, routes);
	}
}
