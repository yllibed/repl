using System.Globalization;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private string[] ResolveShellCompletionCandidates(string line, int cursor)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var state = AnalyzeShellCompletionInput(line, cursor);
		if (state.PriorTokens.Length == 0)
		{
			return [];
		}

		var priorInvocationTokens = state.PriorTokens.Skip(1).ToArray();
		var parsed = InvocationOptionParser.Parse(priorInvocationTokens);
		var commandPrefix = parsed.PositionalArguments.ToArray();
		var currentTokenPrefix = state.CurrentTokenPrefix;
		var currentTokenIsOption = IsGlobalOptionToken(currentTokenPrefix);
		var routeMatch = Resolve(commandPrefix, activeGraph.Routes);
		var hasTerminalRoute = routeMatch is not null && routeMatch.RemainingTokens.Count == 0;
		var commandCandidates = currentTokenIsOption
			? []
			: CollectShellCommandCandidates(commandPrefix, currentTokenPrefix, activeGraph.Routes, activeGraph.Contexts);
		var optionCandidates = currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute)
			? CollectShellOptionCandidates(hasTerminalRoute ? routeMatch!.Route : null, currentTokenPrefix)
			: [];

		return commandCandidates
			.Concat(optionCandidates)
			.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private IEnumerable<string> CollectShellCommandCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var matchingRoutes = CollectVisibleMatchingRoutes(
			commandPrefix,
			StringComparison.OrdinalIgnoreCase,
			routes,
			contexts);
		foreach (var route in matchingRoutes)
		{
			if (commandPrefix.Length >= route.Template.Segments.Count
				|| route.Template.Segments[commandPrefix.Length] is not LiteralRouteSegment literal
				|| !literal.Value.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			yield return literal.Value;
		}
	}

	private IEnumerable<string> CollectShellOptionCandidates(RouteDefinition? route, string currentTokenPrefix)
	{
		foreach (var option in CollectGlobalShellOptionCandidates(currentTokenPrefix))
		{
			yield return option;
		}

		if (route is null)
		{
			yield break;
		}

		foreach (var option in CollectRouteShellOptionCandidates(route, currentTokenPrefix))
		{
			yield return option;
		}
	}

	private IEnumerable<string> CollectGlobalShellOptionCandidates(string currentTokenPrefix)
	{
		var staticOptions = new[]
		{
			"--help",
			"--interactive",
			"--no-interactive",
			"--no-logo",
			"--output:",
		};

		foreach (var option in staticOptions)
		{
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}

		foreach (var alias in _options.Output.Aliases.Keys)
		{
			var option = $"--{alias}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}

		foreach (var format in _options.Output.Transformers.Keys)
		{
			var option = $"--output:{format}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}
	}

	private static IEnumerable<string> CollectRouteShellOptionCandidates(RouteDefinition route, string currentTokenPrefix)
	{
		var routeParameterNames = route.Template.Segments
			.OfType<DynamicRouteSegment>()
			.Select(segment => segment.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var parameter in route.Command.Handler.Method.GetParameters())
		{
			if (string.IsNullOrWhiteSpace(parameter.Name)
				|| parameter.ParameterType == typeof(CancellationToken)
				|| routeParameterNames.Contains(parameter.Name)
				|| IsFrameworkInjectedParameter(parameter.ParameterType)
				|| parameter.GetCustomAttribute<FromContextAttribute>() is not null
				|| parameter.GetCustomAttribute<FromServicesAttribute>() is not null)
			{
				continue;
			}

			var option = $"--{parameter.Name}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}
	}

	private static ShellCompletionInputState AnalyzeShellCompletionInput(string input, int cursor)
	{
		input ??= string.Empty;
		cursor = Math.Clamp(cursor, 0, input.Length);
		var tokens = TokenizeInputSpans(input);
		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (cursor < token.Start || cursor > token.End)
			{
				continue;
			}

			var prior = tokens.Take(i).Select(static tokenSpan => tokenSpan.Value).ToArray();
			var prefix = input[token.Start..cursor];
			return new ShellCompletionInputState(prior, prefix);
		}

		var trailingPrior = tokens
			.Where(token => token.End <= cursor)
			.Select(static token => token.Value)
			.ToArray();
		return new ShellCompletionInputState(trailingPrior, CurrentTokenPrefix: string.Empty);
	}

	private readonly record struct ShellCompletionInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix);

	private string ResolveShellCompletionCommandName()
	{
		var processPath = Environment.ProcessPath;
		if (!string.IsNullOrWhiteSpace(processPath))
		{
			var name = Path.GetFileNameWithoutExtension(processPath);
			if (!string.IsNullOrWhiteSpace(name))
			{
				return name;
			}
		}

		var app = BuildDocumentationApp();
		return string.IsNullOrWhiteSpace(app.Name) ? "repl" : app.Name;
	}
}
