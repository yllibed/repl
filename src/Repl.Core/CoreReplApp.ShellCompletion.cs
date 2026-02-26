using System.Globalization;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private static readonly string[] StaticShellGlobalOptions =
	[
		"--help",
		"--interactive",
		"--no-interactive",
		"--no-logo",
		"--output:",
	];

	private string[] ResolveShellCompletionCandidates(string line, int cursor)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var state = AnalyzeShellCompletionInput(line, cursor);
		if (state.PriorTokens.Length == 0)
		{
			return [];
		}

		var priorTokenCount = state.PriorTokens.Length - 1;
		string[] priorInvocationTokens;
		if (priorTokenCount <= 0)
		{
			priorInvocationTokens = [];
		}
		else
		{
			priorInvocationTokens = new string[priorTokenCount];
			Array.Copy(state.PriorTokens, sourceIndex: 1, priorInvocationTokens, destinationIndex: 0, priorTokenCount);
		}

		var parsed = InvocationOptionParser.Parse(priorInvocationTokens);
		var commandPrefix = parsed.PositionalArguments.ToArray();
		var currentTokenPrefix = state.CurrentTokenPrefix;
		var currentTokenIsOption = IsGlobalOptionToken(currentTokenPrefix);
		var routeMatch = Resolve(commandPrefix, activeGraph.Routes);
		var hasTerminalRoute = routeMatch is not null && routeMatch.RemainingTokens.Count == 0;
		var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var candidates = new List<string>(capacity: 16);
		if (!currentTokenIsOption)
		{
			foreach (var commandCandidate in CollectShellCommandCandidates(
				commandPrefix,
				currentTokenPrefix,
				activeGraph.Routes,
				activeGraph.Contexts))
			{
				TryAddShellCompletionCandidate(commandCandidate, dedupe, candidates);
			}
		}

		if (currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute))
		{
			foreach (var optionCandidate in CollectShellOptionCandidates(
				hasTerminalRoute ? routeMatch!.Route : null,
				currentTokenPrefix))
			{
				TryAddShellCompletionCandidate(optionCandidate, dedupe, candidates);
			}
		}

		candidates.Sort(StringComparer.OrdinalIgnoreCase);
		return [.. candidates];
	}

	private static void TryAddShellCompletionCandidate(
		string candidate,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (string.IsNullOrWhiteSpace(candidate) || !dedupe.Add(candidate))
		{
			return;
		}

		candidates.Add(candidate);
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
		foreach (var option in StaticShellGlobalOptions)
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
		var routeParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var segment in route.Template.Segments)
		{
			if (segment is DynamicRouteSegment dynamicSegment)
			{
				routeParameterNames.Add(dynamicSegment.Name);
			}
		}

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

			var prior = new string[i];
			for (var priorIndex = 0; priorIndex < i; priorIndex++)
			{
				prior[priorIndex] = tokens[priorIndex].Value;
			}

			var prefix = input[token.Start..cursor];
			return new ShellCompletionInputState(prior, prefix);
		}

		var trailingPrior = new List<string>(tokens.Count);
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPrior.Add(token.Value);
			}
		}

		return new ShellCompletionInputState([.. trailingPrior], CurrentTokenPrefix: string.Empty);
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
