namespace Repl;

public sealed partial class CoreReplApp
{
	private RoutingEngine? _routingEngine;
	private RoutingEngine RoutingEng => _routingEngine ??= new(this);

	internal ContextDefinition RegisterContext(string template, Delegate? validation, string? description) =>
		RoutingEng.RegisterContext(template, validation, description);

	internal ValueTask<IReplResult?> ValidateContextsForPathAsync(
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		RoutingEng.ValidateContextsForPathAsync(matchedPathTokens, contexts, serviceProvider, cancellationToken);

	internal ValueTask<IReplResult?> ValidateContextsForMatchAsync(
		RouteMatch match,
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		RoutingEng.ValidateContextsForMatchAsync(match, matchedPathTokens, contexts, serviceProvider, cancellationToken);

	internal List<object?> BuildContextHierarchyValues(
		RouteTemplate matchedRouteTemplate,
		IReadOnlyList<string> matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts) =>
		RoutingEng.BuildContextHierarchyValues(matchedRouteTemplate, matchedPathTokens, contexts);

	internal ValueTask<ContextValidationOutcome> ValidateContextAsync(
		ContextMatch contextMatch,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		RoutingEng.ValidateContextAsync(contextMatch, serviceProvider, cancellationToken);

	internal IReplResult CreateRouteResolutionFailureResult(
		IReadOnlyList<string> tokens,
		RouteResolver.RouteConstraintFailure? constraintFailure,
		RouteResolver.RouteMissingArgumentsFailure? missingArgumentsFailure) =>
		RoutingEng.CreateRouteResolutionFailureResult(tokens, constraintFailure, missingArgumentsFailure);

	internal ValueTask TryRenderCommandBannerAsync(
		CommandBuilder command,
		string? outputFormat,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		RoutingEng.TryRenderCommandBannerAsync(command, outputFormat, serviceProvider, cancellationToken);

	internal bool ShouldRenderBanner(string? requestedOutputFormat) =>
		RoutingEng.ShouldRenderBanner(requestedOutputFormat);

	internal ValueTask InvokeBannerAsync(
		Delegate banner,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		RoutingEng.InvokeBannerAsync(banner, serviceProvider, cancellationToken);

	private string BuildBannerText() =>
		RoutingEng.BuildBannerText();

	internal PrefixResolutionResult ResolveUniquePrefixes(IReadOnlyList<string> tokens) =>
		RoutingEng.ResolveUniquePrefixes(tokens);

	internal RouteDefinition[] ResolveDiscoverableRoutes(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison) =>
		RoutingEng.ResolveDiscoverableRoutes(routes, contexts, scopeTokens, comparison);

	internal ContextDefinition[] ResolveDiscoverableContexts(
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison) =>
		RoutingEng.ResolveDiscoverableContexts(contexts, scopeTokens, comparison);

	internal bool IsRouteSuppressedForDiscovery(
		RouteTemplate routeTemplate,
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison) =>
		RoutingEng.IsRouteSuppressedForDiscovery(routeTemplate, contexts, scopeTokens, comparison);

	internal bool IsContextSuppressedForDiscovery(
		ContextDefinition context,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison) =>
		RoutingEng.IsContextSuppressedForDiscovery(context, scopeTokens, comparison);

	private static IReplResult CreateAmbiguousPrefixResult(PrefixResolutionResult prefixResolution) =>
		RoutingEngine.CreateAmbiguousPrefixResult(prefixResolution);
}
