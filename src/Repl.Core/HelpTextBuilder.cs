using System.ComponentModel;
using System.Reflection;
using System.Text;

namespace Repl;

internal static class HelpTextBuilder
{
	private static readonly string[] HelpRow = ["help [path]", "Show help for current path or a specific path. Aliases: ?"];
	private static readonly string[] UpRow = ["..", "Go up one level in interactive mode."];
	private static readonly string[] ExitRow = ["exit", "Leave interactive mode."];
	private static readonly string[] HistoryRow = ["history [--limit <n>]", "Show recent interactive commands."];
	private static readonly string[] CompleteRow = ["complete --target <name> [--input <text>] <path>", "Resolve completions."];

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

	private static string BuildCommandHelp(RouteDefinition[] routes, bool useAnsi, AnsiPalette palette)
	{
		if (routes.Length == 1)
		{
			return BuildSingleCommandHelp(routes[0], useAnsi, palette);
		}

		var rows = routes
			.Select(route => new[]
			{
				FormatRouteTemplate(route.Template),
				route.Command.Description ?? "No description.",
			})
			.ToArray();
		var style = useAnsi
			? new TextTableStyle((_, column, value) =>
				column == 0
					? AnsiText.Apply(value, palette.CommandStyle)
					: AnsiText.Apply(value, palette.DescriptionStyle))
			: TextTableStyle.None;
		var header = useAnsi
			? AnsiText.Apply("Commands:", palette.SectionStyle)
			: "Commands:";
		var table = TextTableFormatter.FormatRows(
			rows,
			ResolveRenderWidth(),
			includeHeaderSeparator: false,
			style);
		return $"{header}{Environment.NewLine}{table}";
	}

	private static string BuildSingleCommandHelp(RouteDefinition route, bool useAnsi, AnsiPalette palette)
	{
		var displayTemplate = FormatRouteTemplate(route.Template);
		var description = route.Command.Description ?? "No description.";
		var aliases = route.Command.Aliases.Count == 0
			? string.Empty
			: $"{Environment.NewLine}Aliases: {string.Join(", ", route.Command.Aliases)}";
		var paramSection = BuildParameterSection(route, useAnsi, palette);
		if (!useAnsi)
		{
			return $"Usage: {displayTemplate}{Environment.NewLine}Description: {description}{aliases}{paramSection}";
		}

		var usage = $"{AnsiText.Apply("Usage:", palette.SectionStyle)} {AnsiText.Apply(displayTemplate, palette.CommandStyle)}";
		var desc = $"{AnsiText.Apply("Description:", palette.SectionStyle)} {AnsiText.Apply(description, palette.DescriptionStyle)}";
		var aliasText = route.Command.Aliases.Count == 0
			? string.Empty
			: $"{Environment.NewLine}{AnsiText.Apply("Aliases:", palette.SectionStyle)} {AnsiText.Apply(string.Join(", ", route.Command.Aliases), palette.CommandStyle)}";
		return $"{usage}{Environment.NewLine}{desc}{aliasText}{paramSection}";
	}

	private static string BuildParameterSection(RouteDefinition route, bool useAnsi, AnsiPalette palette)
	{
		var dynamicSegments = route.Template.Segments.OfType<DynamicRouteSegment>().ToList();
		if (dynamicSegments.Count == 0)
		{
			return string.Empty;
		}

		var handlerParams = route.Command.Handler.Method.GetParameters();
		var rows = new List<string[]>();
		foreach (var segment in dynamicSegments)
		{
			var param = handlerParams.FirstOrDefault(p =>
				!string.IsNullOrWhiteSpace(p.Name)
				&& string.Equals(p.Name, segment.Name, StringComparison.OrdinalIgnoreCase));
			var desc = param?.GetCustomAttribute<DescriptionAttribute>()?.Description;
			if (desc is not null)
			{
				rows.Add([FormatDynamicSegment(segment), desc]);
			}
		}

		if (rows.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		builder.AppendLine();
		builder.Append(useAnsi
			? AnsiText.Apply("Parameters:", palette.SectionStyle)
			: "Parameters:");
		foreach (var row in rows)
		{
			builder.AppendLine();
			builder.Append(useAnsi
				? $"  {AnsiText.Apply(row[0], palette.CommandStyle)}  {AnsiText.Apply(row[1], palette.DescriptionStyle)}"
				: $"  {row[0]}  {row[1]}");
		}

		return builder.ToString();
	}

	private static string BuildScopeHelp(
		IReadOnlyList<string> scopeTokens,
		RouteDefinition[] routes,
		IReadOnlyList<ContextDefinition> contexts,
		ParsingOptions parsingOptions,
		AmbientCommandOptions ambientOptions,
		int renderWidth,
		bool useAnsi,
		AnsiPalette palette)
	{
		var builder = new StringBuilder();
		var commandRows = BuildScopeCommandRows(routes, contexts, scopeTokens, parsingOptions);
		var scopeRows = BuildScopeContextRows(contexts, scopeTokens, parsingOptions);
		if (commandRows.Length > 0 || scopeRows.Length == 0)
		{
			AppendSectionLine(builder, "Commands:", useAnsi, palette);
			if (commandRows.Length == 0)
			{
				builder.AppendLine("  (none)");
			}
			else
			{
			AppendIndentedRows(builder, commandRows, renderWidth, GetCommandRowsStyle(useAnsi, palette));
			}
		}

		if (scopeRows.Length > 0)
		{
			if (builder.Length > 0)
			{
				builder.AppendLine();
			}

			AppendSectionLine(builder, "Scopes:", useAnsi, palette);
			AppendIndentedRows(builder, scopeRows, renderWidth, GetCommandRowsStyle(useAnsi, palette));
		}

		builder.AppendLine();
		AppendSectionLine(builder, "Global Commands:", useAnsi, palette);
		AppendIndentedRows(builder, BuildGlobalCommandRows(ambientOptions), renderWidth, GetCommandRowsStyle(useAnsi, palette));

		return builder.ToString().TrimEnd();
	}

	private static string[][] BuildScopeCommandRows(
		RouteDefinition[] routes,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions)
	{
		var nextIndex = scopeTokens.Count;
		var groups = routes
			.Where(route => route.Template.Segments.Count > nextIndex)
			.GroupBy(route => route.Template.Segments[nextIndex].RawText, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

		return groups
			.Select(group =>
			{
				var hasScopedContext = contexts.Any(context =>
					MatchesPrefix(context.Template, scopeTokens, parsingOptions)
					&& context.Template.Segments.Count > nextIndex
					&& string.Equals(context.Template.Segments[nextIndex].RawText, group.Key, StringComparison.OrdinalIgnoreCase));
				var hasTerminalCommand = group.Any(route => route.Template.Segments.Count == nextIndex + 1);
				if (hasScopedContext && !hasTerminalCommand)
				{
					return null;
				}

				var display = hasTerminalCommand
					? group.Key
					: BuildScopedCommandDisplay(group, nextIndex, hasScopedContext);
				var description = ResolveScopeDescription(group, contexts, scopeTokens, parsingOptions, nextIndex);
				return new[] { display, description };
			})
			.Where(row => row is not null)
			.Select(row => row!)
			.ToArray();
	}

	private static string[][] BuildScopeContextRows(
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions)
	{
		var nextIndex = scopeTokens.Count;
		return contexts
			.Where(context =>
				MatchesPrefix(context.Template, scopeTokens, parsingOptions)
				&& context.Template.Segments.Count > nextIndex)
			.GroupBy(context => context.Template.Segments[nextIndex].RawText, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.Select(group =>
			{
				var description = group
					.Where(context => context.Template.Segments.Count == nextIndex + 1)
					.Select(context => context.Description)
					.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
					?? group
						.Select(context => context.Description)
						.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
					?? string.Empty;
				return new[] { $"{group.Key} ...", description };
			})
			.ToArray();
	}

	private static string BuildScopedCommandDisplay(
		IGrouping<string, RouteDefinition> group,
		int nextIndex,
		bool hasScopedContext)
	{
		var hasChildren = group.Any(route => route.Template.Segments.Count > nextIndex + 1);
		if (!hasChildren)
		{
			return group.Key;
		}

		if (hasScopedContext)
		{
			return $"{group.Key} ...";
		}

		if (!group.Skip(1).Any())
		{
			var route = group.Single();
			var tail = route.Template.Segments.Skip(nextIndex + 1).ToArray();
			if (tail.Length == 0)
			{
				return group.Key;
			}

			var tailDisplay = string.Join(
				' ',
				tail.Select(segment => segment switch
				{
					LiteralRouteSegment literal => literal.Value,
					DynamicRouteSegment dynamic => FormatDynamicSegment(dynamic),
					_ => string.Empty,
				}).Where(value => !string.IsNullOrWhiteSpace(value)));
			return string.IsNullOrWhiteSpace(tailDisplay)
				? group.Key
				: $"{group.Key} {tailDisplay}";
		}

		return $"{group.Key} ...";
	}

	private static string ResolveScopeDescription(
		IGrouping<string, RouteDefinition> group,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		ParsingOptions parsingOptions,
		int nextIndex) =>
		group
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

	private static string[][] BuildGlobalCommandRows(AmbientCommandOptions ambientOptions)
	{
		var rows = new List<string[]>
		{
			HelpRow,
			UpRow,
		};
		if (ambientOptions.ExitCommandEnabled)
		{
			rows.Add(ExitRow);
		}

		if (ambientOptions.ShowHistoryInHelp)
		{
			rows.Add(HistoryRow);
		}

		if (ambientOptions.ShowCompleteInHelp)
		{
			rows.Add(CompleteRow);
		}

		return [.. rows];
	}

	private static TextTableStyle GetCommandRowsStyle(bool useAnsi, AnsiPalette palette)
	{
		if (!useAnsi)
		{
			return TextTableStyle.None;
		}

		return new TextTableStyle((_, column, value) =>
			column == 0
				? AnsiText.Apply(value, palette.CommandStyle)
				: AnsiText.Apply(value, palette.DescriptionStyle));
	}

	private static void AppendSectionLine(StringBuilder builder, string sectionText, bool useAnsi, AnsiPalette palette)
	{
		builder.AppendLine(useAnsi ? AnsiText.Apply(sectionText, palette.SectionStyle) : sectionText);
	}

	private static void AppendIndentedRows(StringBuilder builder, string[][] rows, int renderWidth, TextTableStyle style)
	{
		var formatted = TextTableFormatter.FormatRows(rows, renderWidth, includeHeaderSeparator: false, style);
		foreach (var line in formatted.Split(Environment.NewLine, StringSplitOptions.None))
		{
			builder.Append("  ").AppendLine(line);
		}
	}

	private static string FormatDynamicSegment(DynamicRouteSegment segment) =>
		segment.IsOptional ? $"[{segment.Name}]" : $"<{segment.Name}>";

	private static string FormatRouteTemplate(RouteTemplate template) =>
		string.Join(' ', template.Segments.Select(s => s switch
		{
			LiteralRouteSegment lit => lit.Value,
			DynamicRouteSegment dyn => FormatDynamicSegment(dyn),
			_ => s.RawText,
		}));

	private static int ResolveRenderWidth()
	{
		if (ReplSessionIO.IsSessionActive && ReplSessionIO.WindowSize is { } size && size.Width > 0)
		{
			return size.Width;
		}

		try
		{
			var width = Console.WindowWidth;
			if (width > 0)
			{
				return width;
			}
		}
		catch
		{
			// Width may be unavailable in redirected output.
		}

		return 120;
	}

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
