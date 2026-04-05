using System.ComponentModel;
using System.Reflection;
using System.Text;
using Repl.Internal.Options;

namespace Repl;

internal static partial class HelpTextBuilder
{
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
		var argumentSection = BuildArgumentSection(route, useAnsi, palette);
		var optionSection = BuildOptionSection(route, useAnsi, palette);
		var answerSection = BuildAnswerSection(route, useAnsi, palette);
		if (!useAnsi)
		{
			return $"Usage: {displayTemplate}{Environment.NewLine}Description: {description}{aliases}{argumentSection}{optionSection}{answerSection}";
		}

		var usage = $"{AnsiText.Apply("Usage:", palette.SectionStyle)} {AnsiText.Apply(displayTemplate, palette.CommandStyle)}";
		var desc = $"{AnsiText.Apply("Description:", palette.SectionStyle)} {AnsiText.Apply(description, palette.DescriptionStyle)}";
		var aliasText = route.Command.Aliases.Count == 0
			? string.Empty
			: $"{Environment.NewLine}{AnsiText.Apply("Aliases:", palette.SectionStyle)} {AnsiText.Apply(string.Join(", ", route.Command.Aliases), palette.CommandStyle)}";
		return $"{usage}{Environment.NewLine}{desc}{aliasText}{argumentSection}{optionSection}{answerSection}";
	}

	private static string BuildArgumentSection(RouteDefinition route, bool useAnsi, AnsiPalette palette)
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
			? AnsiText.Apply("Arguments:", palette.SectionStyle)
			: "Arguments:");
		foreach (var row in rows)
		{
			builder.AppendLine();
			builder.Append(useAnsi
				? $"  {AnsiText.Apply(row[0], palette.CommandStyle)}  {AnsiText.Apply(row[1], palette.DescriptionStyle)}"
				: $"  {row[0]}  {row[1]}");
		}

		return builder.ToString();
	}

	private static string BuildAnswerSection(RouteDefinition route, bool useAnsi, AnsiPalette palette)
	{
		if (route.Command.Answers.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		builder.AppendLine();
		builder.Append(useAnsi
			? AnsiText.Apply("Answers:", palette.SectionStyle)
			: "Answers:");
		foreach (var answer in route.Command.Answers)
		{
			var token = $"--answer:{answer.Name}";
			var desc = answer.Description ?? $"({answer.Type})";
			builder.AppendLine();
			builder.Append(useAnsi
				? $"  {AnsiText.Apply(token, palette.CommandStyle)}  {AnsiText.Apply(desc, palette.DescriptionStyle)}"
				: $"  {token}  {desc}");
		}

		return builder.ToString();
	}

	private static string BuildOptionSection(RouteDefinition route, bool useAnsi, AnsiPalette palette)
	{
		var parameters = route.Command.Handler.Method.GetParameters()
			.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
			.ToDictionary(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);
		var groupProperties = new Dictionary<string, (PropertyInfo Property, object DefaultInstance)>(StringComparer.OrdinalIgnoreCase);
		foreach (var methodParam in route.Command.Handler.Method.GetParameters())
		{
			if (!Attribute.IsDefined(methodParam.ParameterType, typeof(ReplOptionsGroupAttribute), inherit: true))
			{
				continue;
			}

			var defaultInstance = CreateOptionsGroupDefault(methodParam.ParameterType);
			foreach (var prop in GetOptionsGroupProperties(methodParam.ParameterType)
				.Where(prop => prop.CanWrite && !groupProperties.ContainsKey(prop.Name)))
			{
				groupProperties[prop.Name] = (prop, defaultInstance);
			}
		}

		var optionRows = route.OptionSchema.Parameters.Values
			.Where(parameter => parameter.Mode != ReplParameterMode.ArgumentOnly)
			.Select(parameter => BuildOptionRow(route.OptionSchema, parameter, parameters, groupProperties))
			.Where(row => row is not null)
			.Select(row => row!)
			.ToArray();
		if (optionRows.Length == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		builder.AppendLine();
		builder.Append(useAnsi
			? AnsiText.Apply("Options:", palette.SectionStyle)
			: "Options:");
		foreach (var row in optionRows)
		{
			builder.AppendLine();
			builder.Append(useAnsi
				? $"  {AnsiText.Apply(row[0], palette.CommandStyle)}  {AnsiText.Apply(row[1], palette.DescriptionStyle)}"
				: $"  {row[0]}  {row[1]}");
		}

		return builder.ToString();
	}

	private static string[]? BuildOptionRow(
		OptionSchema schema,
		OptionSchemaParameter schemaParameter,
		Dictionary<string, ParameterInfo> parameters,
		Dictionary<string, (PropertyInfo Property, object DefaultInstance)>? groupProperties = null)
	{
		var entries = schema.Entries
			.Where(entry =>
				string.Equals(entry.ParameterName, schemaParameter.Name, StringComparison.OrdinalIgnoreCase)
				&& entry.TokenKind is OptionSchemaTokenKind.NamedOption
					or OptionSchemaTokenKind.BoolFlag
					or OptionSchemaTokenKind.ReverseFlag
					or OptionSchemaTokenKind.ValueAlias
					or OptionSchemaTokenKind.EnumAlias)
			.ToArray();
		if (entries.Length == 0)
		{
			return null;
		}

		var visibleTokens = entries
			.Select(entry => entry.Token)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var tokenDisplay = string.Join(", ", visibleTokens);

		Type parameterType;
		string description;
		string defaultValue;
		if (parameters.TryGetValue(schemaParameter.Name, out var parameter))
		{
			parameterType = parameter.ParameterType;
			description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
			defaultValue = parameter.HasDefaultValue && parameter.DefaultValue is not null
				? $" [default: {parameter.DefaultValue}]"
				: string.Empty;
		}
		else if (groupProperties is not null
			&& groupProperties.TryGetValue(schemaParameter.Name, out var groupInfo))
		{
			parameterType = groupInfo.Property.PropertyType;
			description = groupInfo.Property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
			var propDefault = groupInfo.Property.GetValue(groupInfo.DefaultInstance);
			defaultValue = propDefault is not null && !IsDefaultForType(propDefault, parameterType)
				? $" [default: {propDefault}]"
				: string.Empty;
		}
		else
		{
			return null;
		}

		var placeholder = ResolveOptionPlaceholder(parameterType);
		var left = string.IsNullOrWhiteSpace(placeholder)
			? tokenDisplay
			: $"{tokenDisplay} {placeholder}";
		var right = $"{description}{defaultValue}".Trim();
		return [left, right];
	}

	private static bool IsDefaultForType(object value, Type type)
	{
		if (type == typeof(bool))
		{
			return value is false;
		}

		if (type == typeof(int))
		{
			return value is 0;
		}

		if (type == typeof(long))
		{
			return value is 0L;
		}

		if (type == typeof(double))
		{
			return value is 0.0d;
		}

		return false;
	}

	private static string ResolveOptionPlaceholder(Type parameterType)
	{
		var effectiveType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
		if (effectiveType == typeof(bool))
		{
			return string.Empty;
		}

		if (effectiveType.IsEnum)
		{
			return $"<{string.Join('|', Enum.GetNames(effectiveType))}>";
		}

		return $"<{GetTypePlaceholderName(effectiveType)}>";
	}

	private static string GetTypePlaceholderName(Type type)
	{
		if (type == typeof(string))
		{
			return "string";
		}

		if (type == typeof(int))
		{
			return "int";
		}

		if (type == typeof(long))
		{
			return "long";
		}

		if (type == typeof(Guid))
		{
			return "guid";
		}

		if (type == typeof(FileInfo))
		{
			return "file";
		}

		if (type == typeof(DirectoryInfo))
		{
			return "directory";
		}

		if (type == typeof(ReplDateRange))
		{
			return "date-range";
		}

		if (type == typeof(ReplDateTimeRange))
		{
			return "datetime-range";
		}

		if (type == typeof(ReplDateTimeOffsetRange))
		{
			return "datetimeoffset-range";
		}

		return type.Name.ToLowerInvariant();
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

		var globalOptionRows = BuildGlobalOptionRows(parsingOptions);
		if (globalOptionRows.Length > 0)
		{
			builder.AppendLine();
			AppendSectionLine(builder, "Global Options:", useAnsi, palette);
			AppendIndentedRows(builder, globalOptionRows, renderWidth, GetCommandRowsStyle(useAnsi, palette));
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

		foreach (var cmd in ambientOptions.CustomCommands.Values)
		{
			rows.Add([cmd.Name, cmd.Description ?? string.Empty]);
		}

		return [.. rows];
	}

	private static string[][] BuildGlobalOptionRows(ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(parsingOptions);
		var customRows = parsingOptions.GlobalOptions.Values
			.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
			.Select(option =>
			{
					var aliases = option.Aliases.Count == 0
						? string.Empty
						: $", {string.Join(", ", option.Aliases)}";
				return new[]
				{
					$"{option.CanonicalToken}{aliases}",
					"Custom global option.",
				};
			});
		return [.. BuiltInGlobalOptionRows.Concat(customRows)];
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
}
