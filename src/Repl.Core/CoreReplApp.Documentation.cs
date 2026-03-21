using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Repl.Internal.Options;

namespace Repl;

public sealed partial class CoreReplApp
{
	/// <inheritdoc />
	public ReplDocumentationModel CreateDocumentationModel(string? targetPath = null)
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
			out _);

		var contexts = SelectDocumentationContexts(normalizedTargetPath, commands, discoverableContexts);
		var commandDocs = commands.Select(BuildDocumentationCommand).ToArray();
		var contextDocs = contexts
			.Select(context => new ReplDocContext(
				Path: context.Template.Template,
				Description: context.Description,
				IsDynamic: context.Template.Segments.Any(segment => segment is DynamicRouteSegment),
				IsHidden: context.IsHidden,
				Details: context.Details))
			.ToArray();
		var resourceDocs = commandDocs
			.Where(cmd => cmd.IsResource || cmd.Annotations?.ReadOnly == true)
			.Select(cmd => new ReplDocResource(
				Path: cmd.Path,
				Description: cmd.Description,
				Details: cmd.Details,
				Arguments: cmd.Arguments,
				Options: cmd.Options))
			.ToArray();
		return new ReplDocumentationModel(
			App: BuildDocumentationApp(),
			Contexts: contextDocs,
			Commands: commandDocs,
			Resources: resourceDocs);
	}

	internal ReplDocumentationModel CreateDocumentationModel(
		IServiceProvider serviceProvider,
		string? targetPath = null)
	{
		ArgumentNullException.ThrowIfNull(serviceProvider);

		using var runtimeStateScope = PushRuntimeState(serviceProvider, isInteractiveSession: false);
		return CreateDocumentationModel(targetPath);
	}

	/// <summary>
	/// Internal documentation model creation that supports not-found result for help rendering.
	/// </summary>
	internal object CreateDocumentationModelInternal(string? targetPath)
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
				IsHidden: context.IsHidden,
				Details: context.Details))
			.ToArray();
		var resourceDocs = commandDocs
			.Where(cmd => cmd.IsResource || cmd.Annotations?.ReadOnly == true)
			.Select(cmd => new ReplDocResource(
				Path: cmd.Path,
				Description: cmd.Description,
				Details: cmd.Details,
				Arguments: cmd.Arguments,
				Options: cmd.Options))
			.ToArray();
		return new ReplDocumentationModel(
			App: BuildDocumentationApp(),
			Contexts: contextDocs,
			Commands: commandDocs,
			Resources: resourceDocs);
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
		var handlerParams = route.Command.Handler.Method.GetParameters();
		var arguments = dynamicSegments
			.Select(segment =>
			{
				var paramInfo = handlerParams.FirstOrDefault(p =>
					string.Equals(p.Name, segment.Name, StringComparison.OrdinalIgnoreCase));
				var description = paramInfo?.GetCustomAttribute<DescriptionAttribute>()?.Description;
				return new ReplDocArgument(
					Name: segment.Name,
					Type: GetConstraintTypeName(segment.ConstraintKind),
					Required: !segment.IsOptional,
					Description: description);
			})
			.ToArray();
		var regularOptions = handlerParams
			.Where(parameter =>
				!string.IsNullOrWhiteSpace(parameter.Name)
				&& parameter.ParameterType != typeof(CancellationToken)
				&& !routeParameterNames.Contains(parameter.Name!)
				&& !IsFrameworkInjectedParameter(parameter.ParameterType)
				&& !Attribute.IsDefined(parameter.ParameterType, typeof(ReplOptionsGroupAttribute), inherit: true))
			.Select(parameter => BuildDocumentationOption(route.OptionSchema, parameter));
		var groupOptions = handlerParams
			.Where(parameter => Attribute.IsDefined(parameter.ParameterType, typeof(ReplOptionsGroupAttribute), inherit: true))
			.SelectMany(parameter =>
			{
				var defaultInstance = CreateOptionsGroupDefault(parameter.ParameterType);
				return GetOptionsGroupProperties(parameter.ParameterType)
					.Where(prop => prop.CanWrite)
					.Select(prop => BuildDocumentationOptionFromProperty(route.OptionSchema, prop, defaultInstance));
			});
		var options = regularOptions.Concat(groupOptions).ToArray();

		var answers = BuildDocumentationAnswers(route.Command);

		return new ReplDocCommand(
			Path: route.Template.Template,
			Description: route.Command.Description,
			Aliases: route.Command.Aliases,
			IsHidden: route.Command.IsHidden,
			Arguments: arguments,
			Options: options,
			Details: route.Command.Details,
			Annotations: route.Command.Annotations,
			Metadata: route.Command.Metadata.Count > 0 ? route.Command.Metadata : null,
			Answers: answers.Length > 0 ? answers : null,
			IsResource: route.Command.IsResource,
			IsPrompt: route.Command.IsPrompt);
	}

	private static ReplDocAnswer[] BuildDocumentationAnswers(CommandBuilder command)
	{
		var fluentAnswers = command.Answers
			.Select(a => new ReplDocAnswer(a.Name, a.Type, a.Description));
		var attributeAnswers = command.Handler.Method
			.GetCustomAttributes<AnswerAttribute>()
			.Select(a => new ReplDocAnswer(a.Name, a.Type, a.Description));
		return fluentAnswers
			.Concat(attributeAnswers)
			.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToArray();
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
		|| parameterType == typeof(IReplKeyReader)
		|| string.Equals(parameterType.FullName, "Repl.IMcpClientRoots", StringComparison.Ordinal);

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
				"repldaterange" => "date-range",
				"repldatetimerange" => "datetime-range",
				"repldatetimeoffsetrange" => "datetimeoffset-range",
				_ => type.Name,
			};
		}

		var genericName = type.Name[..type.Name.IndexOf('`')];
		var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
		return $"{genericName}<{genericArgs}>";
	}

	private static ReplDocOption BuildDocumentationOptionFromProperty(
		OptionSchema schema,
		PropertyInfo property,
		object defaultInstance)
	{
		var entries = schema.Entries
			.Where(entry => string.Equals(entry.ParameterName, property.Name, StringComparison.OrdinalIgnoreCase))
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
		var effectiveType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		var enumValues = effectiveType.IsEnum
			? Enum.GetNames(effectiveType)
			: [];
		var propDefault = property.GetValue(defaultInstance);
		var defaultValue = propDefault is not null
			? propDefault.ToString()
			: null;
		return new ReplDocOption(
			Name: property.Name,
			Type: GetFriendlyTypeName(property.PropertyType),
			Required: false,
			Description: property.GetCustomAttribute<DescriptionAttribute>()?.Description,
			Aliases: aliases,
			ReverseAliases: reverseAliases,
			ValueAliases: valueAliases,
			EnumValues: enumValues,
			DefaultValue: defaultValue);
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
}
