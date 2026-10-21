namespace Repl;

/// <summary>
/// Finds the modules whose words an invocation that resolved to no command starts with, matching leading
/// segments by the router's own rules so the two cannot drift.
/// </summary>
internal static class RoutePrefixOwners
{
	/// <summary>
	/// The modules owning the routes, or failing that the contexts, that accept the most leading tokens,
	/// with the score that decided. Leading segments compare literal words first, then parameters that
	/// accept the token, so no number of parameters outweighs one literal word. A context decides only when
	/// it matches more than every route, because the routes it encloses are its own words too. No owner when
	/// no leading token matches anything.
	/// </summary>
	public static (long Score, IReadOnlyList<int> ModuleIds) Find(
		ActiveRoutingGraph graph,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions)
	{
		var (routeScore, routeOwners) = FindBest(
			graph.Routes.Select(route => (Score(route.Template, route, tokens, parsingOptions), route.ModuleId)));
		var (contextScore, contextOwners) = FindBest(
			graph.Contexts.Select(context => (Score(context.Template, route: null, tokens, parsingOptions), context.ModuleId)));
		return contextScore > routeScore ? (contextScore, contextOwners) : (routeScore, routeOwners);
	}

	/// <summary>
	/// How many of the leading tokens <paramref name="route"/>, whose argument failed, addresses, on the scale
	/// of <see cref="Find"/>. The word its parameter rejects counts as that parameter's, since it was typed
	/// where the route expects one, so a sibling whose parameter accepts the word ties it rather than
	/// outscoring it, while a literal word still does. The words after it are not weighed: they may be the
	/// route's own options and arguments.
	/// </summary>
	public static long ScoreFailedRoute(RouteDefinition route, IReadOnlyList<string> tokens, ParsingOptions parsingOptions) =>
		Score(route.Template, route, tokens, parsingOptions, creditRejectedParameter: true);

	/// <summary>The owners with the best non-zero score, each once, and that score; ties keep every owner.</summary>
	public static (long Score, IReadOnlyList<int> ModuleIds) FindBest(IEnumerable<(long Score, int ModuleId)> candidates)
	{
		var bestScore = 0L;
		var moduleIds = new List<int>();
		foreach (var (score, moduleId) in candidates)
		{
			if (score == 0 || score < bestScore)
			{
				continue;
			}

			if (score > bestScore)
			{
				bestScore = score;
				moduleIds.Clear();
			}

			if (!moduleIds.Contains(moduleId))
			{
				moduleIds.Add(moduleId);
			}
		}

		return (bestScore, moduleIds);
	}

	// Literal words in the high half, accepted parameters in the low one: compared as one number, that is
	// literal words first.
	private static long Score(
		RouteTemplate template,
		RouteDefinition? route,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions,
		bool creditRejectedParameter = false)
	{
		var segments = template.Segments;
		var literals = 0L;
		var parameters = 0L;
		for (var i = 0; i < segments.Count && i < tokens.Count; i++)
		{
			switch (segments[i])
			{
				case LiteralRouteSegment literal when MatchesLiteral(route, literal, tokens[i], i, segments.Count):
					literals++;
					continue;
				case DynamicRouteSegment dynamic when RouteConstraintEvaluator.IsMatch(dynamic, tokens[i], parsingOptions):
					parameters++;
					continue;
				case DynamicRouteSegment when creditRejectedParameter:
					parameters++;
					break;
			}

			break;
		}

		return (literals << 32) | parameters;
	}

	/// <summary>
	/// Whether <paramref name="token"/> is the literal word, or for a route's last segment one of its
	/// aliases. A context, <paramref name="route"/> being <see langword="null"/>, has no route of its own and
	/// so no command aliases: its literal words compare as written.
	/// </summary>
	public static bool MatchesLiteral(
		RouteDefinition? route,
		LiteralRouteSegment literal,
		string token,
		int index,
		int segmentCount) =>
		route is not null
			? RouteResolver.IsLiteralMatch(route, literal, token, index, segmentCount, StringComparison.OrdinalIgnoreCase)
			: string.Equals(literal.Value, token, StringComparison.OrdinalIgnoreCase);
}
