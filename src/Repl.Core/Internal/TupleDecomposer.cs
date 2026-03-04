using System.Runtime.CompilerServices;

namespace Repl;

internal static class TupleDecomposer
{
	/// <summary>
	/// Returns true if <paramref name="value"/> is a ValueTuple with 2 or more elements.
	/// </summary>
	internal static bool IsTupleResult(object? value, out ITuple tuple)
	{
		if (value is ITuple t && t.Length >= 2 && IsValueTupleType(value!.GetType()))
		{
			tuple = t;
			return true;
		}

		tuple = default!;
		return false;
	}

	private static bool IsValueTupleType(Type type)
	{
		if (!type.IsValueType || !type.IsGenericType)
		{
			return false;
		}

		var def = type.GetGenericTypeDefinition();
		return def == typeof(ValueTuple<,>)
			|| def == typeof(ValueTuple<,,>)
			|| def == typeof(ValueTuple<,,,>)
			|| def == typeof(ValueTuple<,,,,>)
			|| def == typeof(ValueTuple<,,,,,>)
			|| def == typeof(ValueTuple<,,,,,,>)
			|| def == typeof(ValueTuple<,,,,,,,>);
	}
}
