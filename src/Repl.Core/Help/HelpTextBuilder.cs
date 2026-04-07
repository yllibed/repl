using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Repl.Internal.Options;

namespace Repl;

internal static partial class HelpTextBuilder
{
	private static readonly string[] HelpRow = ["help [path]", "Show help for current path or a specific path. Aliases: ?"];
	private static readonly string[] UpRow = ["..", "Go up one level in interactive mode."];
	private static readonly string[] ExitRow = ["exit", "Leave interactive mode."];
	private static readonly string[] HistoryRow = ["history [--limit <n>]", "Show recent interactive commands."];
	private static readonly string[] CompleteRow = ["complete --target <name> [--input <text>] <path>", "Resolve completions."];
	private static readonly string[][] BuiltInGlobalOptionRows =
	[
		["--help", "Show help for current scope or command."],
		["--interactive", "Force interactive mode."],
		["--no-interactive", "Prevent interactive mode."],
		["--no-logo", "Disable banner rendering."],
		["--output:<format>", "Set output format (for example json, yaml, xml, markdown)."],
		["--answer:<name>[=value]", "Provide prompt answers in non-interactive execution."],
	];

	public static HelpDocumentModel BuildModel(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(routes);
		ArgumentNullException.ThrowIfNull(contexts);
		ArgumentNullException.ThrowIfNull(scopeTokens);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var visibleRoutes = routes
			.Where(route => !route.Command.IsHidden)
			.ToArray();
		var scope = scopeTokens.Count == 0 ? "root" : string.Join(' ', scopeTokens);
		if (TryGetCommandHelpRoutes(visibleRoutes, scopeTokens, parsingOptions, out var commandHelpRoutes))
		{
			return new HelpDocumentModel(
				scope,
				commandHelpRoutes.Select(CreateCommandModel).ToArray(),
				DateTimeOffset.UtcNow);
		}

		var matchingRoutes = visibleRoutes
			.Where(route => MatchesPrefix(route.Template, scopeTokens, parsingOptions))
			.ToArray();
		var commands = BuildGroupedCommandModels(matchingRoutes, contexts, scopeTokens, parsingOptions);
		return new HelpDocumentModel(scope, commands, DateTimeOffset.UtcNow);
	}

	private static HelpCommandModel[] BuildGroupedCommandModels(
		RouteDefinition[] matchingRoutes,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions)
	{
		var nextIndex = scopeTokens.Count;
		return matchingRoutes
			.Where(route => route.Template.Segments.Count > nextIndex)
			.GroupBy(route => route.Template.Segments[nextIndex].RawText, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.Select(group =>
			{
				var hasChildren = group.Any(route => route.Template.Segments.Count > nextIndex + 1);
				var name = hasChildren ? $"{group.Key} ..." : group.Key;
				var description = group
					.Select(route => route.Command.Description)
					.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
					?? contexts
						.Where(context =>
							MatchesPrefix(context.Template, scopeTokens, parsingOptions)
							&& context.Template.Segments.Count > nextIndex
							&& string.Equals(
								context.Template.Segments[nextIndex].RawText,
								group.Key,
								StringComparison.OrdinalIgnoreCase))
						.Select(context => context.Description)
						.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
					?? string.Empty;
				var usage = string.Join(' ', scopeTokens.Append(group.Key));
				var aliases = group
					.SelectMany(route => route.Command.Aliases)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray();
				return new HelpCommandModel(name, description, usage, aliases);
			})
			.ToArray();
	}

	public static string Build(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions,
		AmbientCommandOptions? ambientOptions = null,
		int? renderWidth = null,
		bool useAnsi = false,
		AnsiPalette? palette = null)
	{
		ArgumentNullException.ThrowIfNull(routes);
		ArgumentNullException.ThrowIfNull(contexts);
		ArgumentNullException.ThrowIfNull(scopeTokens);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var visibleRoutes = routes
			.Where(route => !route.Command.IsHidden)
			.ToArray();

		var width = renderWidth ?? ResolveRenderWidth();
		var effectivePalette = palette ?? new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark);
		var effectiveAmbientOptions = ambientOptions ?? new AmbientCommandOptions();
		if (TryGetCommandHelpRoutes(visibleRoutes, scopeTokens, parsingOptions, out var commandHelpRoutes))
		{
			return BuildCommandHelp(commandHelpRoutes, useAnsi, effectivePalette);
		}

		var matchingRoutes = visibleRoutes
			.Where(route => MatchesPrefix(route.Template, scopeTokens, parsingOptions))
			.ToArray();

		return BuildScopeHelp(
			scopeTokens,
			matchingRoutes,
			contexts,
			parsingOptions,
			effectiveAmbientOptions,
			width,
			useAnsi,
			effectivePalette);
	}

	private static bool TryGetCommandHelpRoutes(
		RouteDefinition[] visibleRoutes,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions,
		out RouteDefinition[] routes)
	{
		var exactMatches = visibleRoutes
			.Where(route => IsExactMatch(route.Template, scopeTokens, parsingOptions))
			.ToArray();
		exactMatches = PreferMostSpecificLiteralMatches(exactMatches, scopeTokens);
		if (exactMatches.Length > 0)
		{
			routes = OrderCommandHelpRoutes(exactMatches);
			return true;
		}

		var matchingRoutes = visibleRoutes
			.Where(route => MatchesPrefix(route.Template, scopeTokens, parsingOptions))
			.ToArray();
		matchingRoutes = PreferMostSpecificLiteralMatches(matchingRoutes, scopeTokens);
		if (matchingRoutes.Length == 0)
		{
			routes = [];
			return false;
		}

		var dynamicContinuations = matchingRoutes
			.Where(route =>
				route.Template.Segments.Count > scopeTokens.Count
				&& route.Template.Segments[scopeTokens.Count] is DynamicRouteSegment)
			.ToArray();
		if (dynamicContinuations.Length > 0 && dynamicContinuations.Length == matchingRoutes.Length)
		{
			routes = OrderCommandHelpRoutes(dynamicContinuations);
			return true;
		}

		routes = [];
		return false;
	}

	private static RouteDefinition[] PreferMostSpecificLiteralMatches(
		RouteDefinition[] routes,
		IReadOnlyList<string> scopeTokens)
	{
		if (routes.Length <= 1 || scopeTokens.Count == 0)
		{
			return routes;
		}

		var bestScore = routes
			.Max(route => CountMatchedLiteralSegments(route.Template, scopeTokens));
		return routes
			.Where(route => CountMatchedLiteralSegments(route.Template, scopeTokens) == bestScore)
			.ToArray();
	}

	private static int CountMatchedLiteralSegments(
		RouteTemplate template,
		IReadOnlyList<string> scopeTokens)
	{
		var score = 0;
		var count = Math.Min(template.Segments.Count, scopeTokens.Count);
		for (var i = 0; i < count; i++)
		{
			if (template.Segments[i] is LiteralRouteSegment literal
				&& string.Equals(literal.Value, scopeTokens[i], StringComparison.OrdinalIgnoreCase))
			{
				score++;
			}
		}

		return score;
	}

	private static HelpCommandModel CreateCommandModel(RouteDefinition route)
	{
		var displayTemplate = FormatRouteTemplate(route.Template);
		return new(
			Name: displayTemplate,
			Description: route.Command.Description ?? "No description.",
			Usage: displayTemplate,
			Aliases: route.Command.Aliases.ToArray());
	}

	private static RouteDefinition[] OrderCommandHelpRoutes(RouteDefinition[] routes) =>
		routes
			.OrderBy(route => route.Template.Template, StringComparer.OrdinalIgnoreCase)
			.ThenBy(route => route.Template.Segments.Count)
			.ToArray();

	private static bool IsExactMatch(
		RouteTemplate template,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions)
	{
		if (template.Segments.Count != tokens.Count)
		{
			return false;
		}

		return MatchesPrefix(template, tokens, parsingOptions);
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2067",
		Justification = "Options group types are user-defined and always preserved by the handler delegate reference.")]
	private static object CreateOptionsGroupDefault(Type groupType) =>
		Activator.CreateInstance(groupType)!;

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Options group types are user-defined and always preserved by the handler delegate reference.")]
	private static PropertyInfo[] GetOptionsGroupProperties(Type groupType) =>
		groupType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

	private static bool MatchesPrefix(
		RouteTemplate template,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions)
	{
		if (tokens.Count > template.Segments.Count)
		{
			return false;
		}

		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			var segment = template.Segments[i];
			if (segment is LiteralRouteSegment literal)
			{
				if (!string.Equals(literal.Value, token, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}

				continue;
			}

			var dynamic = (DynamicRouteSegment)segment;
			if (!RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions))
			{
				return false;
			}
		}

		return true;
	}
}
