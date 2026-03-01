using System.Globalization;
using System.Reflection;
using Repl.Internal.Options;

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
		if (!currentTokenIsOption
			&& hasTerminalRoute
			&& TryAddRouteEnumValueCandidates(
				routeMatch!.Route,
				state.PriorTokens,
				currentTokenPrefix,
				dedupe,
				candidates))
		{
			candidates.Sort(StringComparer.OrdinalIgnoreCase);
			return [.. candidates];
		}

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

	private bool TryAddRouteEnumValueCandidates(
		RouteDefinition route,
		string[] priorTokens,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (!TryResolvePendingRouteOption(route, priorTokens, out var entry))
		{
			return false;
		}

		if (!route.OptionSchema.TryGetParameter(entry.ParameterName, out var parameter))
		{
			return false;
		}

		var enumType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!enumType.IsEnum)
		{
			return false;
		}

		var effectiveCaseSensitivity = parameter.CaseSensitivity ?? _options.Parsing.OptionCaseSensitivity;
		var comparison = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var beforeCount = candidates.Count;
		foreach (var enumName in Enum
			         .GetNames(enumType)
			         .Where(name => name.StartsWith(currentTokenPrefix, comparison)))
		{
			TryAddShellCompletionCandidate(enumName, dedupe, candidates);
		}

		return candidates.Count > beforeCount;
	}

	private bool TryResolvePendingRouteOption(
		RouteDefinition route,
		string[] priorTokens,
		out OptionSchemaEntry entry)
	{
		entry = default!;
		if (priorTokens.Length <= 1)
		{
			return false;
		}

		var commandTokens = priorTokens[1..];
		if (commandTokens.Length == 0)
		{
			return false;
		}

		var previousToken = commandTokens[^1];
		if (!IsGlobalOptionToken(previousToken))
		{
			return false;
		}

		var separatorIndex = previousToken.IndexOfAny(['=', ':']);
		if (separatorIndex >= 0)
		{
			return false;
		}

		var matches = route.OptionSchema.ResolveToken(previousToken, _options.Parsing.OptionCaseSensitivity);
		var distinct = matches
			.DistinctBy(candidate => (candidate.ParameterName, candidate.TokenKind, candidate.InjectedValue), ShellOptionSchemaEntryComparer.Instance)
			.ToArray();
		if (distinct.Length != 1)
		{
			return false;
		}

		if (distinct[0].TokenKind is not (OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag))
		{
			return false;
		}

		entry = distinct[0];
		return true;
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
		var comparison = _options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		foreach (var option in StaticShellGlobalOptions)
		{
			if (option.StartsWith(currentTokenPrefix, comparison))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}

		foreach (var alias in _options.Output.Aliases.Keys)
		{
			var option = $"--{alias}";
			if (option.StartsWith(currentTokenPrefix, comparison))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}

		foreach (var format in _options.Output.Transformers.Keys)
		{
			var option = $"--output:{format}";
			if (option.StartsWith(currentTokenPrefix, comparison))
			{
				TryAddShellCompletionCandidate(option, dedupe, candidates);
			}
		}

		foreach (var custom in _options.Parsing.GlobalOptions.Values)
		{
			if (custom.CanonicalToken.StartsWith(currentTokenPrefix, comparison))
			{
				TryAddShellCompletionCandidate(custom.CanonicalToken, dedupe, candidates);
			}

			foreach (var alias in custom.Aliases)
			{
				if (alias.StartsWith(currentTokenPrefix, comparison))
				{
					TryAddShellCompletionCandidate(alias, dedupe, candidates);
				}
			}
		}
	}

	private void AddRouteShellOptionCandidates(
		RouteDefinition route,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		var comparison = _options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		foreach (var token in route.OptionSchema.KnownTokens)
		{
			if (token.StartsWith(currentTokenPrefix, comparison))
			{
				TryAddShellCompletionCandidate(token, dedupe, candidates);
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

	private sealed class ShellOptionSchemaEntryComparer : IEqualityComparer<(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue)>
	{
		public static ShellOptionSchemaEntryComparer Instance { get; } = new();

		public bool Equals(
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) x,
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) y) =>
			string.Equals(x.ParameterName, y.ParameterName, StringComparison.OrdinalIgnoreCase)
			&& x.TokenKind == y.TokenKind
			&& string.Equals(x.InjectedValue, y.InjectedValue, StringComparison.Ordinal);

		public int GetHashCode((string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) obj)
		{
			var parameterHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ParameterName);
			var injectedHash = obj.InjectedValue is null
				? 0
				: StringComparer.Ordinal.GetHashCode(obj.InjectedValue);
			return HashCode.Combine(parameterHash, (int)obj.TokenKind, injectedHash);
		}
	}
}
