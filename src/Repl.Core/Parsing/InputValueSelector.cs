namespace Repl;

internal static class InputValueSelector
{
	internal static bool TrySelectNamed(string name, ReplParameterMode mode,
		IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions, IReadOnlyList<string> positionalArguments,
		int positionalIndex, out IReadOnlyList<string> values)
	{
		if (mode != ReplParameterMode.ArgumentOnly && namedOptions.TryGetValue(name, out var namedValues))
		{
			if (mode == ReplParameterMode.OptionAndPositional && positionalIndex < positionalArguments.Count)
			{
				throw new InvalidOperationException($"Parameter '{name}' cannot receive both named and positional values in the same invocation.");
			}
			values = namedValues;
			return true;
		}
		values = [];
		return false;
	}

	internal static (int Start, int Count) SelectPositional(Type targetType, IReadOnlyList<string> positionalArguments, int positionalIndex)
	{
		var count = HandlerArgumentBinder.TryGetCollectionElementType(targetType, out _)
			? positionalArguments.Count - positionalIndex : Math.Min(1, positionalArguments.Count - positionalIndex);
		return (positionalIndex, Math.Max(0, count));
	}
}
