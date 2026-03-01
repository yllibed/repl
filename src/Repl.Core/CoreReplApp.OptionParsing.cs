namespace Repl;

public sealed partial class CoreReplApp
{
	private static HashSet<string> ResolveKnownHandlerOptionNames(
		Delegate handler,
		IEnumerable<string> routeValueNames)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ArgumentNullException.ThrowIfNull(routeValueNames);

		var routeNames = new HashSet<string>(routeValueNames, StringComparer.OrdinalIgnoreCase);
		var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var parameter in handler.Method.GetParameters())
		{
			if (string.IsNullOrWhiteSpace(parameter.Name)
				|| routeNames.Contains(parameter.Name)
				|| parameter.ParameterType == typeof(CancellationToken))
			{
				continue;
			}

			if (parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true).Length > 0
				|| parameter.GetCustomAttributes(typeof(FromServicesAttribute), inherit: true).Length > 0)
			{
				continue;
			}

			optionNames.Add(parameter.Name);
		}

		return optionNames;
	}

	private static bool TryFindGlobalCommandOptionCollision(
		GlobalInvocationOptions globalOptions,
		HashSet<string> knownOptionNames,
		out string collidingOption)
	{
		foreach (var globalOption in globalOptions.CustomGlobalNamedOptions.Keys)
		{
			if (!knownOptionNames.Contains(globalOption))
			{
				continue;
			}

			collidingOption = $"--{globalOption}";
			return true;
		}

		collidingOption = string.Empty;
		return false;
	}

	private static IReadOnlyDictionary<string, IReadOnlyList<string>> MergeNamedOptions(
		IReadOnlyDictionary<string, IReadOnlyList<string>> commandNamedOptions,
		IReadOnlyDictionary<string, IReadOnlyList<string>> globalNamedOptions)
	{
		if (globalNamedOptions.Count == 0)
		{
			return commandNamedOptions;
		}

		var merged = new Dictionary<string, IReadOnlyList<string>>(
			commandNamedOptions,
			StringComparer.OrdinalIgnoreCase);
		foreach (var pair in globalNamedOptions)
		{
			if (merged.TryGetValue(pair.Key, out var existing))
			{
				var appended = existing.Concat(pair.Value).ToArray();
				merged[pair.Key] = appended;
				continue;
			}

			merged[pair.Key] = pair.Value;
		}

		return merged;
	}
}
