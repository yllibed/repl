namespace Repl;

internal static class InputValueSelector
{
	internal static bool TrySelectNamed(string name, ReplParameterMode mode,
		IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions, IReadOnlyList<string> positionalArguments,
		int positionalIndex, out IReadOnlyList<string> values)
	{
		if (mode != ReplParameterMode.ArgumentOnly && namedOptions.TryGetValue(name, out values!))
		{
			if (mode == ReplParameterMode.OptionAndPositional && positionalIndex < positionalArguments.Count)
			{
				throw new InvalidOperationException($"Parameter '{name}' cannot receive both named and positional values in the same invocation.");
			}
			return true;
		}
		values = [];
		return false;
	}

	internal static IReadOnlyList<string> SelectPositional(Type targetType, IReadOnlyList<string> positionalArguments, int positionalIndex)
	{
		var count = HandlerArgumentBinder.TryGetCollectionElementType(targetType, out _)
			? positionalArguments.Count - positionalIndex : Math.Min(1, positionalArguments.Count - positionalIndex);
		return positionalArguments.Skip(positionalIndex).Take(count).ToArray();
	}
}
