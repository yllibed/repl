using System.ComponentModel;
using System.Reflection;
using Repl.Internal.Options;

namespace Repl;

public sealed partial class CoreReplApp
{
	internal object CreateDocumentationModel(string? targetPath)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var normalizedTargetPath = NormalizePath(targetPath);
		var targetTokens = string.IsNullOrWhiteSpace(normalizedTargetPath)
			? []
			: normalizedTargetPath
				.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var discoverableRoutes = ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			targetTokens,
			StringComparison.OrdinalIgnoreCase);
		var discoverableContexts = ResolveDiscoverableContexts(
			activeGraph.Contexts,
			targetTokens,
			StringComparison.OrdinalIgnoreCase);
		var commands = SelectDocumentationCommands(
			normalizedTargetPath,
			discoverableRoutes,
			discoverableContexts,
			out var notFoundResult);
		if (notFoundResult is not null)
		{
			return notFoundResult;
		}

		var contexts = SelectDocumentationContexts(normalizedTargetPath, commands, discoverableContexts);
		var commandDocs = commands.Select(BuildDocumentationCommand).ToArray();
		var contextDocs = contexts
			.Select(context => new ReplDocContext(
				Path: context.Template.Template,
				Description: context.Description,
				IsDynamic: context.Template.Segments.Any(segment => segment is DynamicRouteSegment),
				IsHidden: context.IsHidden))
			.ToArray();
		return new ReplDocumentationModel(
			App: BuildDocumentationApp(),
			Contexts: contextDocs,
			Commands: commandDocs);
	}

	private static RouteDefinition[] SelectDocumentationCommands(
		string? normalizedTargetPath,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		out IReplResult? notFoundResult)
	{
		notFoundResult = null;
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return routes.Where(route => !route.Command.IsHidden).ToArray();
		}

		var exactCommand = routes.FirstOrDefault(
			route => string.Equals(
				route.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactCommand is not null)
		{
			return [exactCommand];
		}

		var exactContext = contexts.FirstOrDefault(
			context => string.Equals(
				context.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactContext is not null)
		{
			return routes
				.Where(route =>
					!route.Command.IsHidden
					&& route.Template.Template.StartsWith(
						$"{exactContext.Template.Template} ",
						StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}

		notFoundResult = Results.NotFound($"Documentation target '{normalizedTargetPath}' not found.");
		return [];
	}

	private static ContextDefinition[] SelectDocumentationContexts(
		string? normalizedTargetPath,
		RouteDefinition[] commands,
		IReadOnlyList<ContextDefinition> contexts)
	{
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return [.. contexts];
		}

		var exactContext = contexts.FirstOrDefault(
			context => string.Equals(
				context.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactContext is not null)
		{
			return [exactContext];
		}

		if (commands.Length == 0)
		{
			return [];
		}

		var selected = contexts
			.Where(context => commands.Any(command =>
				command.Template.Template.StartsWith(
					$"{context.Template.Template} ",
					StringComparison.OrdinalIgnoreCase)
				|| string.Equals(
					command.Template.Template,
					context.Template.Template,
					StringComparison.OrdinalIgnoreCase)))
			.ToArray();
		return selected;
	}

	private ReplDocCommand BuildDocumentationCommand(RouteDefinition route)
	{
		var dynamicSegments = route.Template.Segments
			.OfType<DynamicRouteSegment>()
			.ToArray();
		var routeParameterNames = dynamicSegments
			.Select(segment => segment.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var arguments = dynamicSegments
			.Select(segment => new ReplDocArgument(
				Name: segment.Name,
				Type: GetConstraintTypeName(segment.ConstraintKind),
				Required: !segment.IsOptional,
				Description: null))
			.ToArray();

		var options = route.Command.Handler.Method
			.GetParameters()
			.Where(parameter =>
				!string.IsNullOrWhiteSpace(parameter.Name)
				&& parameter.ParameterType != typeof(CancellationToken)
				&& !routeParameterNames.Contains(parameter.Name!)
				&& !IsFrameworkInjectedParameter(parameter.ParameterType))
			.Select(parameter => BuildDocumentationOption(route.OptionSchema, parameter))
			.ToArray();

		return new ReplDocCommand(
			Path: route.Template.Template,
			Description: route.Command.Description,
			Aliases: route.Command.Aliases,
			IsHidden: route.Command.IsHidden,
			Arguments: arguments,
			Options: options);
	}

	private ReplDocApp BuildDocumentationApp()
	{
		var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
		var name = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
			?? assembly.GetName().Name
			?? "repl";
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString();
		var description = _description
			?? assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
		return new ReplDocApp(name, version, description);
	}

	private static bool IsFrameworkInjectedParameter(Type parameterType) =>
		parameterType == typeof(IServiceProvider)
		|| parameterType == typeof(ICoreReplApp)
		|| parameterType == typeof(CoreReplApp)
		|| parameterType == typeof(IReplSessionState)
		|| parameterType == typeof(IReplInteractionChannel)
		|| parameterType == typeof(IReplIoContext)
		|| parameterType == typeof(IReplKeyReader);

	private static bool IsRequiredParameter(ParameterInfo parameter)
	{
		if (parameter.HasDefaultValue)
		{
			return false;
		}

		if (!parameter.ParameterType.IsValueType)
		{
			return false;
		}

		return Nullable.GetUnderlyingType(parameter.ParameterType) is null;
	}

	private static string GetConstraintTypeName(RouteConstraintKind kind) =>
		kind switch
		{
			RouteConstraintKind.String => "string",
			RouteConstraintKind.Alpha => "string",
			RouteConstraintKind.Bool => "bool",
			RouteConstraintKind.Email => "email",
			RouteConstraintKind.Uri => "uri",
			RouteConstraintKind.Url => "url",
			RouteConstraintKind.Urn => "urn",
			RouteConstraintKind.Time => "time",
			RouteConstraintKind.Date => "date",
			RouteConstraintKind.DateTime => "datetime",
			RouteConstraintKind.DateTimeOffset => "datetimeoffset",
			RouteConstraintKind.TimeSpan => "timespan",
			RouteConstraintKind.Guid => "guid",
			RouteConstraintKind.Long => "long",
			RouteConstraintKind.Int => "int",
			RouteConstraintKind.Custom => "custom",
			_ => "string",
		};

	private static string GetFriendlyTypeName(Type type)
	{
		var underlying = Nullable.GetUnderlyingType(type);
		if (underlying is not null)
		{
			return $"{GetFriendlyTypeName(underlying)}?";
		}

		if (type.IsEnum)
		{
			return string.Join('|', Enum.GetNames(type));
		}

		if (!type.IsGenericType)
		{
			return type.Name.ToLowerInvariant() switch
			{
				"string" => "string",
				"int32" => "int",
				"int64" => "long",
				"boolean" => "bool",
				"double" => "double",
				"decimal" => "decimal",
				"dateonly" => "date",
				"datetime" => "datetime",
				"timeonly" => "time",
				"datetimeoffset" => "datetimeoffset",
				"timespan" => "timespan",
				_ => type.Name,
			};
		}

		var genericName = type.Name[..type.Name.IndexOf('`')];
		var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
		return $"{genericName}<{genericArgs}>";
	}

	private static ReplDocOption BuildDocumentationOption(OptionSchema schema, ParameterInfo parameter)
	{
		var entries = schema.Entries
			.Where(entry => string.Equals(entry.ParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		var aliases = entries
			.Where(entry => entry.TokenKind is OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag)
			.Select(entry => entry.Token)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var reverseAliases = entries
			.Where(entry => entry.TokenKind == OptionSchemaTokenKind.ReverseFlag)
			.Select(entry => entry.Token)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var valueAliases = entries
			.Where(entry => entry.TokenKind is OptionSchemaTokenKind.ValueAlias or OptionSchemaTokenKind.EnumAlias)
			.Select(entry => new ReplDocValueAlias(entry.Token, entry.InjectedValue ?? string.Empty))
			.GroupBy(alias => alias.Token, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
		var effectiveType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		var enumValues = effectiveType.IsEnum
			? Enum.GetNames(effectiveType)
			: [];
		var defaultValue = parameter.HasDefaultValue && parameter.DefaultValue is not null
			? parameter.DefaultValue.ToString()
			: null;
		return new ReplDocOption(
			Name: parameter.Name!,
			Type: GetFriendlyTypeName(parameter.ParameterType),
			Required: IsRequiredParameter(parameter),
			Description: parameter.GetCustomAttribute<DescriptionAttribute>()?.Description,
			Aliases: aliases,
			ReverseAliases: reverseAliases,
			ValueAliases: valueAliases,
			EnumValues: enumValues,
			DefaultValue: defaultValue);
	}
}
