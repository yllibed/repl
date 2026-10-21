using System.Collections.Immutable;

namespace Repl;

public sealed partial class CoreReplApp
{
	// The codes each module applies, already merged with those it inherits from the modules that mapped it.
	// Written while modules are mapped, which happens on one thread, like the rest of the registrations.
	private ImmutableDictionary<int, ReplExitCodeOverrides> _moduleExitCodes =
		ImmutableDictionary<int, ReplExitCodeOverrides>.Empty;

	private void RegisterModuleExitCodes(int moduleId, int parentModuleId, ReplExitCodeOverrides? declared)
	{
		var inherited = _moduleExitCodes.GetValueOrDefault(parentModuleId);
		if ((declared?.InheritFrom(inherited) ?? inherited) is { } effective)
		{
			_moduleExitCodes = _moduleExitCodes.SetItem(moduleId, effective);
		}
	}

	/// <summary>
	/// Attributes an outcome to the module whose command the invocation addressed, so the codes that module
	/// declared apply before the application's table.
	/// </summary>
	private ExecutionOutcome AttributeTo(in ExecutionOutcome outcome, int moduleId, string? route) =>
		outcome with { ModuleExitCodes = _moduleExitCodes.GetValueOrDefault(moduleId), Route = route };

	/// <summary>
	/// Decides the refusal of an invocation that resolved to no command and the module it belongs to: the
	/// owners <see cref="RoutePrefixOwners.Find"/> finds, so <c>tests bogus</c> is refused as the
	/// <c>tests</c> module would refuse it. A route whose argument failed keeps the refusal, its argument's
	/// error, unless another route or a context matches more of the words typed: the failed constraint
	/// first, then the missing argument. When no failure is addressed that way, the invocation is refused
	/// as an unknown command among those words, so the message names what the exit code follows.
	/// </summary>
	internal UnresolvedRefusal DecideUnresolvedRefusal(
		ActiveRoutingGraph graph,
		IReadOnlyList<string> tokens,
		int scopeTokenCount,
		RouteResolver.RouteResolutionResult resolution)
	{
		var owners = RoutePrefixOwners.Find(graph, tokens, _options.Parsing);
		return FindAddressedFailure(graph, tokens, scopeTokenCount, owners.Score, resolution) switch
		{
			{ Constraint: { } constraint } => new(
				RoutingEngine.CreateInvalidRouteValueResult(constraint),
				constraint.Route.ModuleId,
				constraint.Route.Template.Template),
			{ Missing: { } missing } => new(
				RoutingEngine.CreateMissingRouteValuesResult(missing),
				missing.Route.ModuleId,
				missing.Route.Template.Template),
			_ => new(CreateUnknownCommandResult(tokens, graph), ResolveOwner(owners.ModuleIds), Route: null),
		};
	}

	/// <summary>Whether <see cref="DecideUnresolvedRefusal"/> refuses with an argument's failed constraint.</summary>
	internal bool RefusesWithAConstraintFailure(ActiveRoutingGraph graph, IReadOnlyList<string> tokens, int scopeTokenCount,
		RouteResolver.RouteResolutionResult resolution) =>
		FindAddressedFailure(
			graph, tokens, scopeTokenCount, RoutePrefixOwners.Find(graph, tokens, _options.Parsing).Score, resolution)
			.Constraint is not null;

	// The failure the words typed address. Routing ranks failures on its own scale while ownership counts
	// literal words first, so its pick may be outscored by another failed route: the routes that no owner
	// outscores are resolved again on their own, their failed constraint first, then their missing argument.
	// When routing saw no failure at all, no route failed.
	private AddressedFailure FindAddressedFailure(ActiveRoutingGraph graph, IReadOnlyList<string> tokens,
		int scopeTokenCount, long ownerScore, RouteResolver.RouteResolutionResult resolution)
	{
		if (resolution.ConstraintFailure is null && resolution.MissingArgumentsFailure is null)
		{
			return default;
		}

		// A native request names its route, resolved from its values rather than these words: its failure is
		// the refusal.
		if (_currentInput.Value?.Request is { Command: not null })
		{
			return resolution.ConstraintFailure is { } requested
				? new(requested, Missing: null)
				: new(Constraint: null, resolution.MissingArgumentsFailure);
		}

		var addressed = graph.Routes
			.Where(route => RoutePrefixOwners.ScoreFailedRoute(route, tokens, scopeTokenCount, _options.Parsing) >= ownerScore)
			.ToArray();
		if (addressed.Length == 0)
		{
			return default;
		}

		var own = RouteResolver.ResolveWithDiagnostics(addressed, tokens, _options.Parsing);
		return own.ConstraintFailure is { } constraint
			? new(constraint, Missing: null)
			: new(Constraint: null, own.MissingArgumentsFailure);
	}

	private readonly record struct AddressedFailure(
		RouteResolver.RouteConstraintFailure? Constraint,
		RouteResolver.RouteMissingArgumentsFailure? Missing);

	/// <summary>Attributes a refusal to the module and route <see cref="DecideUnresolvedRefusal"/> decided.</summary>
	internal ExecutionOutcome AttributeUnresolved(in ExecutionOutcome refusal, UnresolvedRefusal unresolved) =>
		AttributeTo(refusal, unresolved.ModuleId, unresolved.Route);

	/// <summary>The refusal of an invocation that resolved to no command, with the module and route it belongs to.</summary>
	internal readonly record struct UnresolvedRefusal(IReplResult Failure, int ModuleId, string? Route);

	/// <summary>
	/// Attributes the refusal of an ambiguous abbreviation to the modules owning what it could expand to,
	/// weighed as an unknown command is (see <see cref="RoutePrefixOwners.Find"/>),
	/// so <c>t run</c> between a module's <c>tests run</c> and <c>tools run</c> is refused as that module
	/// would refuse it.
	/// </summary>
	/// <remarks>
	/// Rather than visiting every combination of expansions, each route or context leads to the expansion
	/// that follows its own words: prefix resolution expands the input, and wherever it leaves a word
	/// ambiguous the template's own word, or alias, is chosen, until it is settled or the template can go no
	/// further; only the words settled by then count. Each such expansion is weighed as an unknown command
	/// is, a route outranking a context within it, and the expansions matching the most words decide
	/// together. Prefix resolution does the expanding, so hidden commands, suppressed ones and exact words
	/// weigh as they do when routing. Where the template has a parameter instead, the first candidate its
	/// constraint accepts, in ordinal order, is taken: another candidate could resolve later words
	/// differently, and following each would bring the combinations back.
	/// </remarks>
	internal ExecutionOutcome AttributeAmbiguous(
		in ExecutionOutcome refusal,
		ActiveRoutingGraph graph,
		PrefixResolutionResult prefix)
	{
		var resolutions = new Dictionary<(PrefixResolutionResult From, string Choice), PrefixResolutionResult>();
		var weighed = new HashSet<(PrefixResolutionResult Resolution, int SettledCount)>();
		var owners = new List<(long Score, int ModuleId)>();
		var templates = graph.Routes.Select(static route => (route.Template, Route: (RouteDefinition?)route))
			.Concat(graph.Contexts.Select(static context => (context.Template, Route: (RouteDefinition?)null)));
		foreach (var (template, route) in templates)
		{
			var expansion = ExpandToward(template, route, prefix, graph, resolutions);
			if (!weighed.Add(expansion))
			{
				continue;
			}

			var (score, moduleIds) = RoutePrefixOwners.Find(
				graph,
				new ArraySegment<string>(expansion.Resolution.Tokens, 0, expansion.SettledCount),
				_options.Parsing);
			owners.AddRange(moduleIds.Select(moduleId => (score, moduleId)));
		}

		return AttributeTo(refusal, ResolveOwner(RoutePrefixOwners.FindBest(owners).ModuleIds), route: null);
	}

	// The resolution following the template's own words wherever prefix resolution leaves a word
	// ambiguous, with how many of its tokens are settled: a word left ambiguous, and those after it, are
	// not something routing would read. Each choice settles a word further right than the last, so this
	// takes at most one resolution per word; templates sharing a path share its resolutions, and so the
	// expansion they lead to.
	private (PrefixResolutionResult Resolution, int SettledCount) ExpandToward(
		RouteTemplate template,
		RouteDefinition? route,
		PrefixResolutionResult prefix,
		ActiveRoutingGraph graph,
		Dictionary<(PrefixResolutionResult From, string Choice), PrefixResolutionResult> resolutions)
	{
		var current = prefix;
		while (current.IsAmbiguous && current.AmbiguousIndex < template.Segments.Count)
		{
			var index = current.AmbiguousIndex;
			var choice = template.Segments[index] switch
			{
				LiteralRouteSegment literal => current.Candidates.FirstOrDefault(candidate =>
					RoutePrefixOwners.MatchesLiteral(route, literal, candidate, index, template.Segments.Count)),
				DynamicRouteSegment dynamic => current.Candidates.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(candidate =>
					RouteConstraintEvaluator.IsMatch(dynamic, candidate, _options.Parsing)),
				_ => null,
			};
			if (choice is null)
			{
				break;
			}

			// The same resolution and the same choice always lead to the same next resolution.
			if (!resolutions.TryGetValue((current, choice), out var next))
			{
				string[] expanded = [.. current.Tokens];
				expanded[index] = choice;
				next = ResolveUniquePrefixes(expanded, graph);
				resolutions.Add((current, choice), next);
			}

			current = next;
		}

		return (current, current.IsAmbiguous ? current.AmbiguousIndex : current.Tokens.Length);
	}

	/// <summary>Attributes the refusal of a context's validation to the module that mapped the context.</summary>
	internal ExecutionOutcome AttributeToContext(in ExecutionOutcome refusal, ContextDefinition context) =>
		AttributeTo(refusal, context.ModuleId, route: null);

	/// <summary>
	/// The module that decides for tied owners: any of them when they all apply the same codes, otherwise
	/// their nearest common ancestor, which is the application when they share none.
	/// </summary>
	private int ResolveOwner(IReadOnlyList<int> owners)
	{
		if (owners.Count == 0)
		{
			return 0;
		}

		var codes = _moduleExitCodes.GetValueOrDefault(owners[0]);
		return owners.All(owner => Equals(_moduleExitCodes.GetValueOrDefault(owner), codes))
			? owners[0]
			: owners.Aggregate(FindNearestCommonAncestor);
	}

	private int FindNearestCommonAncestor(int first, int second)
	{
		var ancestorsOfFirst = new HashSet<int> { 0 };
		for (var moduleId = first; moduleId != 0; moduleId = FindParentModuleId(moduleId))
		{
			ancestorsOfFirst.Add(moduleId);
		}

		var candidate = second;
		while (!ancestorsOfFirst.Contains(candidate))
		{
			candidate = FindParentModuleId(candidate);
		}

		return candidate;
	}

	private int FindParentModuleId(int moduleId)
	{
		foreach (var registration in _moduleRegistrations)
		{
			if (registration.ModuleId == moduleId)
			{
				return registration.ParentModuleId;
			}
		}

		return 0;
	}
}
