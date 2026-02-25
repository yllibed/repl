using System.Globalization;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private void RegisterContext(string template, Delegate? validation, string? description)
	{
		var parsedTemplate = RouteTemplateParser.Parse(template, _options.Parsing);
		var moduleId = ResolveCurrentMappingModuleId();
		RouteConfigurationValidator.ValidateUnique(
			parsedTemplate,
			_contexts
				.Where(context => context.ModuleId == moduleId)
				.Select(context => context.Template)
		);

		_contexts.Add(new ContextDefinition(parsedTemplate, validation, description, moduleId));
	}

	private async ValueTask<IReplResult?> ValidateContextsForPathAsync(
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextMatches = ContextResolver.ResolvePrefixes(contexts, matchedPathTokens, _options.Parsing);
		foreach (var contextMatch in contextMatches)
		{
			var validation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
			{
				return validation.Failure;
			}
		}

		return null;
	}

	private async ValueTask<IReplResult?> ValidateContextsForMatchAsync(
		RouteMatch match,
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextMatches = ResolveRouteContextPrefixes(match.Route.Template, matchedPathTokens, contexts);
		foreach (var contextMatch in contextMatches)
		{
			var validation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
			{
				return validation.Failure;
			}
		}

		return null;
	}

	private List<object?> BuildContextHierarchyValues(
		RouteTemplate matchedRouteTemplate,
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var matches = ResolveRouteContextPrefixes(matchedRouteTemplate, matchedPathTokens, contexts);
		var values = new List<object?>();
		foreach (var contextMatch in matches)
		{
			foreach (var dynamicSegment in contextMatch.Context.Template.Segments.OfType<DynamicRouteSegment>())
			{
				if (!contextMatch.RouteValues.TryGetValue(dynamicSegment.Name, out var routeValue))
				{
					continue;
				}

				values.Add(ConvertContextValue(routeValue, dynamicSegment.ConstraintKind));
			}
		}

		return values;
	}

	private IReadOnlyList<ContextMatch> ResolveRouteContextPrefixes(
		RouteTemplate matchedRouteTemplate,
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var matches = ContextResolver.ResolvePrefixes(contexts, matchedPathTokens, _options.Parsing);
		return [..
			matches.Where(contextMatch =>
				IsTemplatePrefix(
					contextMatch.Context.Template,
					matchedRouteTemplate)),
		];
	}

	private static bool IsTemplatePrefix(RouteTemplate contextTemplate, RouteTemplate routeTemplate)
	{
		if (contextTemplate.Segments.Count > routeTemplate.Segments.Count)
		{
			return false;
		}

		for (var i = 0; i < contextTemplate.Segments.Count; i++)
		{
			var contextSegment = contextTemplate.Segments[i];
			var routeSegment = routeTemplate.Segments[i];
			if (!AreSegmentsEquivalent(contextSegment, routeSegment))
			{
				return false;
			}
		}

		return true;
	}

	private static bool AreSegmentsEquivalent(RouteSegment left, RouteSegment right)
	{
		if (left is LiteralRouteSegment leftLiteral && right is LiteralRouteSegment rightLiteral)
		{
			return string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.OrdinalIgnoreCase);
		}

		if (left is DynamicRouteSegment leftDynamic && right is DynamicRouteSegment rightDynamic)
		{
			if (leftDynamic.ConstraintKind != rightDynamic.ConstraintKind)
			{
				return false;
			}

			if (leftDynamic.ConstraintKind != RouteConstraintKind.Custom)
			{
				return true;
			}

			return string.Equals(
				leftDynamic.CustomConstraintName,
				rightDynamic.CustomConstraintName,
				StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private static object? ConvertContextValue(string routeValue, RouteConstraintKind kind) =>
		kind switch
		{
			RouteConstraintKind.Int => ParameterValueConverter.ConvertSingle(routeValue, typeof(int), CultureInfo.InvariantCulture),
			RouteConstraintKind.Long => ParameterValueConverter.ConvertSingle(routeValue, typeof(long), CultureInfo.InvariantCulture),
			RouteConstraintKind.Bool => ParameterValueConverter.ConvertSingle(routeValue, typeof(bool), CultureInfo.InvariantCulture),
			RouteConstraintKind.Guid => ParameterValueConverter.ConvertSingle(routeValue, typeof(Guid), CultureInfo.InvariantCulture),
			RouteConstraintKind.Uri => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Url => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Urn => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Time => ParameterValueConverter.ConvertSingle(routeValue, typeof(TimeOnly), CultureInfo.InvariantCulture),
			RouteConstraintKind.Date => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateOnly), CultureInfo.InvariantCulture),
			RouteConstraintKind.DateTime => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateTime), CultureInfo.InvariantCulture),
			RouteConstraintKind.DateTimeOffset => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateTimeOffset), CultureInfo.InvariantCulture),
			RouteConstraintKind.TimeSpan => ParameterValueConverter.ConvertSingle(routeValue, typeof(TimeSpan), CultureInfo.InvariantCulture),
			_ => routeValue,
		};

	private async ValueTask<ContextValidationOutcome> ValidateContextAsync(
		ContextMatch contextMatch,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (contextMatch.Context.Validation is null)
		{
			return ContextValidationOutcome.Success;
		}

		var bindingContext = new InvocationBindingContext(
			contextMatch.RouteValues,
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			[],
			[],
			_options.Parsing.NumericFormatProvider,
			serviceProvider,
			_options.Interaction,
			cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(contextMatch.Context.Validation, bindingContext);
		var validationResult = await CommandInvoker
			.InvokeAsync(contextMatch.Context.Validation, arguments)
			.ConfigureAwait(false);
		return validationResult switch
		{
			bool value => value
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(CreateDefaultContextValidationFailure(contextMatch)),
			IReplResult replResult => string.Equals(replResult.Kind, "text", StringComparison.OrdinalIgnoreCase)
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(replResult),
			string text => string.IsNullOrWhiteSpace(text)
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(Results.Validation(text)),
			null => ContextValidationOutcome.FromFailure(CreateDefaultContextValidationFailure(contextMatch)),
			_ => throw new InvalidOperationException(
				"Context validation must return bool, string, IReplResult, or null."),
		};
	}

	private static IReplResult CreateDefaultContextValidationFailure(ContextMatch contextMatch)
	{
		var scope = contextMatch.Context.Template.Template;
		var details = contextMatch.RouteValues.Count == 0
			? null
			: contextMatch.RouteValues;
		return Results.Validation($"Scope validation failed for '{scope}'.", details);
	}

	private IReplResult CreateUnknownCommandResult(IReadOnlyList<string> tokens)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var input = string.Join(' ', tokens);
		var visibleRoutes = activeGraph.Routes
			.Where(route => !route.Command.IsHidden)
			.Select(route => route.Template.Template)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var bestSuggestion = FindBestSuggestion(input, visibleRoutes);
		if (bestSuggestion is null)
		{
			return Results.Error("unknown_command", $"Unknown command '{input}'.");
		}

		return Results.Error(
			code: "unknown_command",
			message: $"Unknown command '{input}'. Did you mean '{bestSuggestion}'?");
	}

	private static IReplResult CreateAmbiguousPrefixResult(PrefixResolutionResult prefixResolution)
	{
		var message = $"Ambiguous command prefix '{prefixResolution.AmbiguousToken}'. Candidates: {string.Join(", ", prefixResolution.Candidates)}.";
		return Results.Validation(message);
	}

	private static IReplResult CreateInvalidRouteValueResult(RouteResolver.RouteConstraintFailure failure)
	{
		var expected = GetConstraintDisplayName(failure.Segment);
		var message = $"Invalid value '{failure.Value}' for parameter '{failure.Segment.Name}' (expected: {expected}).";
		return Results.Validation(message);
	}

	private static IReplResult CreateMissingRouteValuesResult(RouteResolver.RouteMissingArgumentsFailure failure)
	{
		if (failure.MissingSegments.Length == 1)
		{
			var segment = failure.MissingSegments[0];
			var expected = GetConstraintDisplayName(segment);
			var message = $"Missing value for parameter '{segment.Name}' (expected: {expected}).";
			return Results.Validation(message);
		}

		var names = string.Join(", ", failure.MissingSegments.Select(segment => segment.Name));
		return Results.Validation($"Missing values for parameters: {names}.");
	}

	private IReplResult CreateRouteResolutionFailureResult(
		IReadOnlyList<string> tokens,
		RouteResolver.RouteConstraintFailure? constraintFailure,
		RouteResolver.RouteMissingArgumentsFailure? missingArgumentsFailure)
	{
		if (constraintFailure is { } routeConstraintFailure)
		{
			return CreateInvalidRouteValueResult(routeConstraintFailure);
		}

		if (missingArgumentsFailure is { } routeMissingArgumentsFailure)
		{
			return CreateMissingRouteValuesResult(routeMissingArgumentsFailure);
		}

		return CreateUnknownCommandResult(tokens);
	}

	private static string GetConstraintDisplayName(DynamicRouteSegment segment) =>
		segment.ConstraintKind == RouteConstraintKind.Custom && !string.IsNullOrWhiteSpace(segment.CustomConstraintName)
			? segment.CustomConstraintName!
			: GetConstraintTypeName(segment.ConstraintKind);

	private async ValueTask TryRenderCommandBannerAsync(
		CommandBuilder command,
		string? outputFormat,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (command.Banner is { } banner && ShouldRenderBanner(outputFormat))
		{
			await InvokeBannerAsync(banner, serviceProvider, cancellationToken).ConfigureAwait(false);
		}
	}

	private bool ShouldRenderBanner(string? requestedOutputFormat)
	{
		if (_allBannersSuppressed.Value || !_options.Output.BannerEnabled)
		{
			return false;
		}

		var format = string.IsNullOrWhiteSpace(requestedOutputFormat)
			? _options.Output.DefaultFormat
			: requestedOutputFormat;
		return string.Equals(format, "human", StringComparison.OrdinalIgnoreCase);
	}

	private async ValueTask InvokeBannerAsync(
		Delegate banner,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var bindingContext = new InvocationBindingContext(
			routeValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			namedOptions: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			positionalArguments: [],
			contextValues: [ReplSessionIO.Output],
			numericFormatProvider: _options.Parsing.NumericFormatProvider,
			serviceProvider: serviceProvider,
			interactionOptions: _options.Interaction,
			cancellationToken: cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(banner, bindingContext);
		var result = await CommandInvoker.InvokeAsync(banner, arguments).ConfigureAwait(false);
		if (result is string text && !string.IsNullOrEmpty(text))
		{
			var styled = _options.Output.IsAnsiEnabled()
				? AnsiText.Apply(text, _options.Output.ResolvePalette().BannerStyle)
				: text;
			await ReplSessionIO.Output.WriteLineAsync(styled).ConfigureAwait(false);
		}
	}

	private string BuildBannerText()
	{
		var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
		var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var description = _description
			?? assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;

		var header = string.Join(
			' ',
			new[] { product, version }
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Select(value => value!));

		if (string.IsNullOrWhiteSpace(header))
		{
			return description ?? string.Empty;
		}

		return string.IsNullOrWhiteSpace(description)
			? header
			: $"{header}{Environment.NewLine}{description}";
	}

	private PrefixResolutionResult ResolveUniquePrefixes(IReadOnlyList<string> tokens)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		if (tokens.Count == 0)
		{
			return new PrefixResolutionResult(tokens: []);
		}

		var resolved = tokens.ToArray();
		for (var index = 0; index < resolved.Length; index++)
		{
			// Prefix expansion is only attempted on literal nodes that remain reachable
			// after validating previously resolved segments (including typed dynamics).
			var candidates = ResolveLiteralCandidatesAtIndex(resolved, index, activeGraph.Routes, activeGraph.Contexts);
			if (candidates.Length == 0)
			{
				continue;
			}

			var token = resolved[index];
			var exact = candidates
				.Where(candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (exact.Length == 1)
			{
				resolved[index] = exact[0];
				continue;
			}

			var prefixMatches = candidates
				.Where(candidate => candidate.StartsWith(token, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			if (prefixMatches.Length == 1)
			{
				resolved[index] = prefixMatches[0];
				continue;
			}

			if (prefixMatches.Length > 1)
			{
				// Ambiguous shorthand must fail fast so users don't execute the wrong command.
				return new PrefixResolutionResult(
					tokens: resolved,
					ambiguousToken: token,
					candidates: prefixMatches);
			}
		}

		return new PrefixResolutionResult(tokens: resolved);
	}

	private string[] ResolveLiteralCandidatesAtIndex(
		string[] tokens,
		int index,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var literals = EnumeratePrefixTemplates(routes, contexts)
			.Where(template => !template.IsHidden)
			.SelectMany(template => GetCandidateLiterals(template, tokens, index))
			.Where(candidate => !string.IsNullOrWhiteSpace(candidate))
			.Select(candidate => candidate!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return literals;
	}

	private static IEnumerable<PrefixTemplate> EnumeratePrefixTemplates(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		foreach (var route in routes)
		{
			yield return new PrefixTemplate(route.Template, route.Command.IsHidden, route.Command.Aliases);
		}

		foreach (var context in contexts)
		{
			yield return new PrefixTemplate(context.Template, IsHidden: false, Aliases: []);
		}
	}

	private IReadOnlyList<string> GetCandidateLiterals(PrefixTemplate template, string[] tokens, int index)
	{
		var routeTemplate = template.Template;
		if (routeTemplate.Segments.Count <= index)
		{
			return [];
		}

		for (var i = 0; i < index; i++)
		{
			var token = tokens[i];
			var segment = routeTemplate.Segments[i];
			// Keep only templates whose resolved prefix still matches the user's input.
			if (segment is LiteralRouteSegment literal
				&& !string.Equals(literal.Value, token, StringComparison.OrdinalIgnoreCase))
			{
				return [];
			}

			if (segment is DynamicRouteSegment dynamic
				&& !RouteConstraintEvaluator.IsMatch(dynamic, token, _options.Parsing))
			{
				return [];
			}
		}

		if (routeTemplate.Segments[index] is not LiteralRouteSegment literalSegment)
		{
			return [];
		}

		if (index == routeTemplate.Segments.Count - 1 && template.Aliases.Count > 0)
		{
			return [literalSegment.Value, .. template.Aliases];
		}

		return [literalSegment.Value];
	}

	private static string? FindBestSuggestion(string input, string[] candidates)
	{
		if (string.IsNullOrWhiteSpace(input) || candidates.Length == 0)
		{
			return null;
		}

		var exactPrefix = candidates
			.FirstOrDefault(candidate =>
				candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(exactPrefix))
		{
			return exactPrefix;
		}

		var normalizedInput = input.ToLowerInvariant();
		var minDistance = int.MaxValue;
		string? best = null;
		foreach (var candidate in candidates)
		{
			var distance = ComputeLevenshteinDistance(
				normalizedInput,
				candidate.ToLowerInvariant());
			if (distance < minDistance)
			{
				minDistance = distance;
				best = candidate;
			}
		}

		var threshold = Math.Max(2, normalizedInput.Length / 3);
		return minDistance <= threshold ? best : null;
	}
}
