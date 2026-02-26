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

		var parsed = state.PriorTokens.Length <= 1
			? InvocationOptionParser.Parse(Array.Empty<string>())
			: InvocationOptionParser.Parse(new ArraySegment<string>(
				state.PriorTokens,
				offset: 1,
				count: state.PriorTokens.Length - 1));
		var commandPrefix = parsed.PositionalArguments as string[] ?? [.. parsed.PositionalArguments];
		var currentTokenPrefix = state.CurrentTokenPrefix;
		var currentTokenIsOption = IsGlobalOptionToken(currentTokenPrefix);
		var routeMatch = Resolve(commandPrefix, activeGraph.Routes);
		var hasTerminalRoute = routeMatch is not null && routeMatch.RemainingTokens.Count == 0;
		var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var candidates = new List<string>(capacity: 16);
		if (!currentTokenIsOption)
		{
			AddShellCommandCandidates(
				commandPrefix,
				currentTokenPrefix,
				activeGraph.Routes,
				activeGraph.Contexts,
				dedupe,
				candidates);
		}

		if (currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute))
		{
			AddShellOptionCandidates(
				hasTerminalRoute ? routeMatch!.Route : null,
				currentTokenPrefix,
				dedupe,
				candidates);
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

	private void AddShellCommandCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		HashSet<string> dedupe,
		List<string> candidates)
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

			TryAddShellCompletionCandidate(literal.Value, dedupe, candidates);
		}
	}

	private void AddShellOptionCandidates(
		RouteDefinition? route,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		AddGlobalShellOptionCandidates(currentTokenPrefix, dedupe, candidates);

		if (route is null)
		{
			return;
		}

		AddRouteShellOptionCandidates(route, currentTokenPrefix, dedupe, candidates);
	}

	private void AddGlobalShellOptionCandidates(
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		foreach (var option in StaticShellGlobalOptions)
		{
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}

		foreach (var alias in _options.Output.Aliases.Keys)
		{
			var option = $"--{alias}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}

		foreach (var format in _options.Output.Transformers.Keys)
		{
			var option = $"--output:{format}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}
	}

	private static void AddRouteShellOptionCandidates(
		RouteDefinition route,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
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
				TryAddShellCompletionCandidate(option, dedupe, candidates);
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

		var trailingPriorCount = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPriorCount++;
			}
		}

		if (trailingPriorCount == 0)
		{
			return new ShellCompletionInputState([], CurrentTokenPrefix: string.Empty);
		}

		var trailingPrior = new string[trailingPriorCount];
		var index = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPrior[index++] = token.Value;
			}
		}

		return new ShellCompletionInputState(trailingPrior, CurrentTokenPrefix: string.Empty);
	}

	private readonly record struct ShellCompletionInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix);

	internal static string ResolveShellCompletionCommandName(
		IReadOnlyList<string>? commandLineArgs,
		string? processPath,
		string? fallbackName)
	{
		if (commandLineArgs is { Count: > 0 })
		{
			var commandHead = TryGetCommandHead(commandLineArgs[0]);
			if (!string.IsNullOrWhiteSpace(commandHead))
			{
				return commandHead;
			}
		}

		var processHead = TryGetCommandHead(processPath);
		if (!string.IsNullOrWhiteSpace(processHead))
		{
			return processHead;
		}

		return string.IsNullOrWhiteSpace(fallbackName) ? "repl" : fallbackName;
	}

	private static string? TryGetCommandHead(string? pathLike)
	{
		if (string.IsNullOrWhiteSpace(pathLike))
		{
			return null;
		}

		var fileName = Path.GetFileName(pathLike.Trim());
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		foreach (var extension in KnownExecutableExtensions)
		{
			if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				var head = fileName[..^extension.Length];
				return string.IsNullOrWhiteSpace(head) ? null : head;
			}
		}

		return fileName;
	}

	private static readonly string[] KnownExecutableExtensions =
	[
		".exe",
		".cmd",
		".bat",
		".com",
		".ps1",
		".dll",
	];

	private string ResolveShellCompletionCommandName()
	{
		var app = BuildDocumentationApp();
		return ResolveShellCompletionCommandName(
			Environment.GetCommandLineArgs(),
			Environment.ProcessPath,
			app.Name);
	}
}
