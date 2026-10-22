using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Repl.Interaction;
using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Generates documentation models from the Repl routing graph.
/// </summary>
internal sealed class DocumentationEngine(CoreReplApp app)
{
	// Keep dispatch metadata out of public record equality and serialization.
	private static readonly ConditionalWeakTable<ReplDocOption, string> OptionParameterNames = new();

	internal static string ResolveInvocationParameterName(ReplDocOption option) =>
		OptionParameterNames.TryGetValue(option, out var name) ? name : option.Name;

	/// <summary>
	/// Creates a documentation model for the given target path.
	/// </summary>
	public ReplDocumentationModel CreateDocumentationModel(string? targetPath = null)
	{
		var (model, _) = CreateDocumentationModelCore(targetPath);
		return model;
	}

	/// <summary>
	/// Creates a documentation model with an explicit service provider for runtime state.
	/// </summary>
	public ReplDocumentationModel CreateDocumentationModel(
		IServiceProvider serviceProvider,
		string? targetPath = null)
	{
		ArgumentNullException.ThrowIfNull(serviceProvider);

		using var runtimeStateScope = app.PushRuntimeState(serviceProvider, isInteractiveSession: false);
		return CreateDocumentationModel(targetPath);
	}

	internal ReplDocumentationModel CreateDocumentationModel(
		IServiceProvider serviceProvider,
		Func<ReplDocCommand, bool> commandFilter)
	{
		ArgumentNullException.ThrowIfNull(serviceProvider);
		ArgumentNullException.ThrowIfNull(commandFilter);

		using var runtimeStateScope = app.PushRuntimeState(serviceProvider, isInteractiveSession: false);
		var (model, _) = CreateDocumentationModelCore(targetPath: null, commandFilter);
		return model;
	}

	/// <summary>
	/// Internal documentation model creation that supports not-found result for help rendering.
	/// </summary>
	public object CreateDocumentationModelInternal(string? targetPath)
	{
		var (model, notFoundResult) = CreateDocumentationModelCore(targetPath);
		return notFoundResult is null ? model : notFoundResult;
	}

	private (ReplDocumentationModel Model, IReplResult? NotFoundResult) CreateDocumentationModelCore(
		string? targetPath,
		Func<ReplDocCommand, bool>? commandFilter = null)
	{
		var activeGraph = app.ResolveActiveRoutingGraph();
		var normalizedTargetPath = NormalizePath(targetPath);
		var targetTokens = string.IsNullOrWhiteSpace(normalizedTargetPath)
			? []
			: normalizedTargetPath
				.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var discoverableRoutes = app.ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			targetTokens,
			StringComparison.OrdinalIgnoreCase);
		var discoverableContexts = app.ResolveDiscoverableContexts(
			activeGraph.Contexts,
			targetTokens,
			StringComparison.OrdinalIgnoreCase);
		var commands = SelectDocumentationCommands(
			normalizedTargetPath,
			discoverableRoutes,
			discoverableContexts,
			out var notFoundResult);

		var contexts = SelectDocumentationContexts(normalizedTargetPath, commands, discoverableContexts);

		// Mirrors the command axis: a targeted command exports its hidden options, flagged, the way
		// a targeted hidden command exports itself. Aggregate models omit them — which is also what
		// keeps them out of the MCP tool schema and its argument allow-list, since MCP always builds
		// the aggregate model.
		var isExactCommandTarget = commands.Length == 1
			&& !string.IsNullOrWhiteSpace(normalizedTargetPath)
			&& string.Equals(commands[0].Template.Template, normalizedTargetPath, StringComparison.OrdinalIgnoreCase);
		var serviceAvailability = new Dictionary<Type, bool>();
		var customGlobalOwnership = GlobalOptionParser.BuildCustomTokenOwnership(app.OptionsSnapshot.Parsing);
		var commandDocs = BuildDocumentationCommands(
			commands,
			isExactCommandTarget,
			serviceAvailability,
			customGlobalOwnership,
			commandFilter);
		var contextDocs = contexts
			.Select(context => new ReplDocContext(
				Path: context.Template.Template,
				Description: context.Description,
				IsDynamic: context.Template.Segments.Any(segment => segment is DynamicRouteSegment),
				IsHidden: context.IsHidden,
				Details: context.Details))
			.ToArray();
		var resourceDocs = BuildDocumentationResources(commandDocs);
		var model = new ReplDocumentationModel(
			App: BuildDocumentationApp(),
			Contexts: contextDocs,
			Commands: commandDocs,
			Resources: resourceDocs);
		return (model, notFoundResult);
	}

	private ReplDocCommand[] BuildDocumentationCommands(
		RouteDefinition[] routes,
		bool includeHiddenOptions,
		Dictionary<Type, bool> serviceAvailability,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership,
		Func<ReplDocCommand, bool>? commandFilter)
	{
		var commands = new List<ReplDocCommand>(routes.Length);
		foreach (var route in routes)
		{
			var command = BuildDocumentationCommand(
				route,
				includeHiddenOptions,
				validateInvocability: commandFilter is null,
				serviceAvailability,
				customGlobalOwnership);
			if (commandFilter is not null && !commandFilter(command))
			{
				continue;
			}

			if (commandFilter is not null && !includeHiddenOptions)
			{
				ValidateDocumentationInvocability(route, serviceAvailability, customGlobalOwnership);
			}

			commands.Add(command);
		}

		return [.. commands];
	}

	private static ReplDocResource[] BuildDocumentationResources(IReadOnlyList<ReplDocCommand> commands) =>
		commands
			.Where(static command => command.IsResource || command.Annotations?.ReadOnly == true)
			.Select(static command => new ReplDocResource(
				Path: command.Path,
				Description: command.Description,
				Details: command.Details,
				Arguments: command.Arguments,
				Options: command.Options))
			.ToArray();

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

	private ReplDocCommand BuildDocumentationCommand(
		RouteDefinition route,
		bool includeHiddenOptions,
		bool validateInvocability,
		Dictionary<Type, bool> serviceAvailability,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		if (!includeHiddenOptions && validateInvocability)
		{
			ValidateDocumentationInvocability(route, serviceAvailability, customGlobalOwnership);
		}

		var dynamicSegments = route.Template.Segments
			.OfType<DynamicRouteSegment>()
			.ToArray();
		var handlerParams = route.Command.Handler.Method.GetParameters();
		var arguments = BuildDocumentationArguments(dynamicSegments, handlerParams);
		var options = BuildDocumentationOptions(
			route,
			handlerParams,
			includeHiddenOptions,
			serviceAvailability,
			customGlobalOwnership);
		var answers = BuildDocumentationAnswers(route.Command);
		var acceptsPagingInput = handlerParams.Any(static parameter => parameter.ParameterType == typeof(IReplPagingContext));
		var emitsPagedResult = IsPagedReturnType(route.Command.Handler.Method.ReturnType);

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
			IsPrompt: route.Command.IsPrompt,
			AcceptsPagingInput: acceptsPagingInput,
			EmitsPagedResult: emitsPagedResult);
	}

	private ReplDocOption[] BuildDocumentationOptions(
		RouteDefinition route,
		ParameterInfo[] handlerParams,
		bool includeHiddenOptions,
		Dictionary<Type, bool> serviceAvailability,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		var schema = route.OptionSchema;
		var regularOptions = handlerParams
			.Where(parameter =>
				schema.IsOptionParameter(parameter)
				&& (includeHiddenOptions || !schema.IsOptionHidden(parameter.Name!))
				&& ShouldIncludeDocumentationOption(
					route, parameter.Name!, includeHiddenOptions, customGlobalOwnership))
			.Select(parameter => BuildDocumentationOption(
				schema, parameter, serviceAvailability, customGlobalOwnership));
		var groupOptions = schema.OptionsGroupTypes
			.SelectMany(groupType =>
			{
				var defaultInstance = CreateOptionsGroupDefault(groupType);
				return GetOptionsGroupProperties(groupType)
					.Where(prop =>
						prop.CanWrite
						&& (includeHiddenOptions || !schema.IsOptionHidden(prop.Name))
						&& ShouldIncludeDocumentationOption(
							route, prop.Name, includeHiddenOptions, customGlobalOwnership))
					.Select(prop => BuildDocumentationOptionFromProperty(
						schema, prop, defaultInstance, customGlobalOwnership));
			});
		return regularOptions.Concat(groupOptions).ToArray();
	}

	private static ReplDocArgument[] BuildDocumentationArguments(
		DynamicRouteSegment[] dynamicSegments,
		ParameterInfo[] handlerParams) =>
		dynamicSegments
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

	private static bool IsPagedReturnType(Type returnType)
	{
		var effectiveType = UnwrapAsyncReturnType(returnType);
		return typeof(IReplPage).IsAssignableFrom(effectiveType)
			|| typeof(IReplPageSource).IsAssignableFrom(effectiveType);
	}

	private static Type UnwrapAsyncReturnType(Type returnType)
	{
		if (!returnType.IsGenericType)
		{
			return returnType;
		}

		var definition = returnType.GetGenericTypeDefinition();
		return definition == typeof(Task<>) || definition == typeof(ValueTask<>)
			? returnType.GetGenericArguments()[0]
			: returnType;
	}

	internal ReplDocApp BuildDocumentationApp()
	{
		var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
		var name = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
			?? assembly.GetName().Name
			?? "repl";
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString();
		var description = app.Description
			?? assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
		return new ReplDocApp(name, version, description);
	}

	// Explicit lower bounds and non-omittable CLR shapes are required only when the binding path
	// cannot obtain the value from the active provider. This must use GetService itself — not
	// registration metadata — because custom/external providers and null-returning factories are
	// part of the same contract as HandlerArgumentBinder.
	internal static bool IsRequiredOption(
		OptionSchema schema,
		string parameterName,
		IServiceProvider serviceProvider,
		Dictionary<Type, bool> serviceAvailability)
	{
		if (!schema.TryGetParameter(parameterName, out var parameter))
		{
			return false;
		}

		var requiresFallback = parameter.ExplicitArity is ReplArity.OneOrMore or ReplArity.ExactlyOne
			|| !parameter.CanBeOmitted;
		return requiresFallback
			&& (!parameter.SupportsServiceFallback
				|| !IsServiceAvailable(parameter.ParameterType, serviceProvider, serviceAvailability));
	}

	private static bool IsServiceAvailable(
		Type serviceType,
		IServiceProvider serviceProvider,
		Dictionary<Type, bool> serviceAvailability)
	{
		if (serviceAvailability.TryGetValue(serviceType, out var available))
		{
			return available;
		}

		// GetService can activate a transient factory or throw outright — a scoped registration
		// resolved from a root provider under ValidateScopes is a common way this happens. A
		// misbehaving registration must not crash discovery for every other route over one option's
		// fallback check; treat a throw here the same as a null result, unavailable.
		try
		{
			available = serviceProvider.GetService(serviceType) is not null;
		}
		catch (Exception)
		{
			available = false;
		}

		serviceAvailability[serviceType] = available;
		return available;
	}

	private static bool ShouldIncludeDocumentationOption(
		RouteDefinition route,
		string parameterName,
		bool includeHiddenOptions,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		var schema = route.OptionSchema;
		if (includeHiddenOptions && schema.IsOptionHidden(parameterName))
		{
			return true;
		}

		var displayToken = schema.ResolveDisplayToken(parameterName);
		var hasReachableToken = schema.ResolveDiscoverableAliases(parameterName)
			.Any(entry => !customGlobalOwnership.ContainsKey(entry.Token));
		if (displayToken is null || hasReachableToken)
		{
			return true;
		}

		return false;
	}

	private static bool HasExplicitRequiredArity(OptionSchema schema, string parameterName) =>
		schema.TryGetParameter(parameterName, out var schemaParameter)
		&& schemaParameter.ExplicitArity is ReplArity.OneOrMore or ReplArity.ExactlyOne;

	private void ValidateDocumentationInvocability(
		RouteDefinition route,
		Dictionary<Type, bool> serviceAvailability,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		ValidateHiddenOptionInvocability(route, serviceAvailability);

		var schema = route.OptionSchema;
		// The schema holds every option exactly once: the handler's own and its expanded groups' properties.
		foreach (var parameterName in schema.Parameters.Keys)
		{
			if (schema.IsOptionHidden(parameterName)
				|| ShouldIncludeDocumentationOption(
					route,
					parameterName,
					includeHiddenOptions: false,
					customGlobalOwnership)
				|| !IsRequiredOption(schema, parameterName, app.CurrentServiceProvider, serviceAvailability))
			{
				continue;
			}

			throw new HiddenRequiredOptionException(
				parameterName,
				schema.ResolveDisplayToken(parameterName),
				route.Template.Template);
		}
	}

	private void ValidateHiddenOptionInvocability(
		RouteDefinition route,
		Dictionary<Type, bool> serviceAvailability)
	{
		var schema = route.OptionSchema;
		foreach (var parameter in schema.Parameters.Values)
		{
			if (parameter.Mode == ReplParameterMode.ArgumentOnly
				|| !parameter.IsHidden
				|| !IsRequiredOption(schema, parameter.Name, app.CurrentServiceProvider, serviceAvailability))
			{
				continue;
			}

			throw new HiddenRequiredOptionException(
				parameter.Name,
				schema.ResolveDisplayToken(parameter.Name),
				route.Template.Template);
		}
	}

	internal static string GetConstraintTypeName(RouteConstraintKind kind) =>
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
		object defaultInstance,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		var displayToken = schema.ResolveDisplayToken(property.Name);
		var entries = schema.ResolveDiscoverableAliases(property.Name)
			.Where(entry => !customGlobalOwnership.ContainsKey(entry.Token))
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
		var defaultValue = ParsingOptions.FormatValue(property.GetValue(defaultInstance));
		var option = new ReplDocOption(
			Name: displayToken?.TrimStart('-') ?? property.Name,
			Type: GetFriendlyTypeName(property.PropertyType),
			Required: HasExplicitRequiredArity(schema, property.Name),
			Description: property.GetCustomAttribute<DescriptionAttribute>()?.Description,
			Aliases: aliases,
			ReverseAliases: reverseAliases,
			ValueAliases: valueAliases,
			EnumValues: enumValues,
			DefaultValue: defaultValue)
		{
			IsHidden = schema.IsOptionHidden(property.Name),
			IsAutomationHidden = schema.IsOptionAutomationHidden(property.Name),
		};
		OptionParameterNames.Add(option, property.Name);
		return option;
	}

	private ReplDocOption BuildDocumentationOption(
		OptionSchema schema,
		ParameterInfo parameter,
		Dictionary<Type, bool> serviceAvailability,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership)
	{
		var displayToken = schema.ResolveDisplayToken(parameter.Name!);
		var entries = schema.ResolveDiscoverableAliases(parameter.Name!)
			.Where(entry => !customGlobalOwnership.ContainsKey(entry.Token))
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
		var option = new ReplDocOption(
			Name: displayToken?.TrimStart('-') ?? parameter.Name!,
			Type: GetFriendlyTypeName(parameter.ParameterType),
			Required: IsRequiredOption(schema, parameter.Name!, app.CurrentServiceProvider, serviceAvailability),
			Description: parameter.GetCustomAttribute<DescriptionAttribute>()?.Description,
			Aliases: aliases,
			ReverseAliases: reverseAliases,
			ValueAliases: valueAliases,
			EnumValues: enumValues,
			DefaultValue: defaultValue)
		{
			IsHidden = schema.IsOptionHidden(parameter.Name!),
			IsAutomationHidden = schema.IsOptionAutomationHidden(parameter.Name!),
		};
		OptionParameterNames.Add(option, parameter.Name!);
		return option;
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

	private static string? NormalizePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		var parts = path
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Length == 0
			? null
			: string.Join(' ', parts);
	}
}
