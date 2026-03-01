using System.Diagnostics.CodeAnalysis;
using Repl.Internal.Options;

namespace Repl;

internal static class HandlerArgumentBinder
{
	public static object?[] Bind(Delegate handler, InvocationBindingContext context)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ArgumentNullException.ThrowIfNull(context);

		var parameters = handler.Method.GetParameters();
		var values = new object?[parameters.Length];
		var positionalIndex = 0;

		for (var i = 0; i < parameters.Length; i++)
		{
			var parameter = parameters[i];
			values[i] = BindParameter(parameter, context, ref positionalIndex);
		}

		return values;
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Binding precedence must stay explicit to preserve predictable runtime behavior.")]
	private static object? BindParameter(
		System.Reflection.ParameterInfo parameter,
		InvocationBindingContext context,
		ref int positionalIndex)
	{
		var parameterName = parameter.Name ?? string.Empty;
		var bindingMode = ResolveBindingMode(parameter, context.OptionSchema);
		var enumIgnoreCase = ResolveEnumIgnoreCase(parameterName, context);

		if (parameter.ParameterType == typeof(CancellationToken))
		{
			return context.CancellationToken;
		}

		if (HasExplicitBindingDirection(parameter))
		{
			if (TryResolveFromContextOrServices(parameter, context, out var explicitValue))
			{
				return explicitValue;
			}

			throw new InvalidOperationException(
				$"Unable to bind parameter '{parameter.Name}' ({parameter.ParameterType.Name}).");
		}

		if (context.RouteValues.TryGetValue(parameterName, out var routeValue))
		{
			return ParameterValueConverter.ConvertSingle(
				routeValue,
				parameter.ParameterType,
				context.NumericFormatProvider,
				enumIgnoreCase);
		}

		if (bindingMode != ReplParameterMode.ArgumentOnly
			&& context.NamedOptions.TryGetValue(parameterName, out var namedValues))
		{
			if (bindingMode == ReplParameterMode.OptionAndPositional
				&& CanConsumePositional(parameter, context.PositionalArguments, positionalIndex))
			{
				throw new InvalidOperationException(
					$"Parameter '{parameterName}' cannot receive both named and positional values in the same invocation.");
			}

				return ConvertManyOrSingle(
					parameter,
					namedValues,
					parameter.ParameterType,
					context.NumericFormatProvider,
					enumIgnoreCase);
			}

		if (TryResolveFromContextOrServices(parameter, context, out var resolved))
		{
			return resolved;
		}

		if (bindingMode != ReplParameterMode.OptionOnly
			&& TryConsumePositional(
			parameter,
			context.PositionalArguments,
			context.NumericFormatProvider,
			enumIgnoreCase,
			ref positionalIndex,
			out var positionalValue))
		{
			return positionalValue;
		}

		if (parameter.HasDefaultValue)
		{
			return parameter.DefaultValue;
		}

		if (!parameter.ParameterType.IsValueType
			|| Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
		{
			return null;
		}

		throw new InvalidOperationException(
			$"Unable to bind parameter '{parameter.Name}' ({parameter.ParameterType.Name}).");
	}

	private static bool HasExplicitBindingDirection(System.Reflection.ParameterInfo parameter) =>
		parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true).Length > 0
		|| parameter.GetCustomAttributes(typeof(FromServicesAttribute), inherit: true).Length > 0;

	private static bool TryResolveFromContextOrServices(
		System.Reflection.ParameterInfo parameter,
		InvocationBindingContext context,
		out object? resolved)
	{
		var fromContext = parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true)
			.Cast<FromContextAttribute>().SingleOrDefault();
		var hasFromContext = fromContext is not null;
		var fromServices = parameter.GetCustomAttributes(typeof(FromServicesAttribute), inherit: true)
			.Cast<FromServicesAttribute>().SingleOrDefault();
		var hasFromServices = fromServices is not null;
		if (hasFromContext && hasFromServices)
		{
			throw new InvalidOperationException(
				$"Parameter '{parameter.Name}' cannot declare both [FromContext] and [FromServices].");
		}

		if (hasFromContext)
		{
			return ResolveExplicitFromContext(parameter, context.ContextValues, fromContext!, out resolved);
		}

		if (hasFromServices)
		{
			return ResolveExplicitFromServices(parameter, context.ServiceProvider, fromServices!, out resolved);
		}

		return ResolveImplicitFromContextOrServices(parameter, context, out resolved);
	}

	private static bool ResolveExplicitFromContext(
		System.Reflection.ParameterInfo parameter,
		IReadOnlyList<object?> contextValues,
		FromContextAttribute fromContext,
		out object? resolved)
	{
		if (fromContext.All)
		{
			if (TryResolveAllFromContext(parameter.ParameterType, contextValues, out resolved))
			{
				return true;
			}

			throw new InvalidOperationException(
				$"Unable to bind parameter '{parameter.Name}' from all matching context values.");
		}

		if (TryResolveFromContext(parameter.ParameterType, contextValues, out resolved))
		{
			return true;
		}

		throw new InvalidOperationException(
			$"Unable to bind parameter '{parameter.Name}' from context values.");
	}

	private static bool ResolveExplicitFromServices(
		System.Reflection.ParameterInfo parameter,
		IServiceProvider serviceProvider,
		FromServicesAttribute fromServices,
		out object? resolved)
	{
		resolved = ResolveService(parameter.ParameterType, serviceProvider, fromServices.Key);
		if (resolved is not null)
		{
			return true;
		}

		var source = string.IsNullOrWhiteSpace(fromServices.Key)
			? "services"
			: $"services with key '{fromServices.Key}'";
		throw new InvalidOperationException(
			$"Unable to resolve parameter '{parameter.Name}' from {source}.");
	}

	private static bool ResolveImplicitFromContextOrServices(
		System.Reflection.ParameterInfo parameter,
		InvocationBindingContext context,
		out object? resolved)
	{
		if (InteractionProgressFactory.TryCreate(parameter.ParameterType, context, out resolved))
		{
			return true;
		}

		var foundContext = TryResolveFromContext(parameter.ParameterType, context.ContextValues, out var contextValue);
		var serviceValue = context.ServiceProvider.GetService(parameter.ParameterType);
		if (foundContext && serviceValue is not null)
		{
			throw new InvalidOperationException(
				$"Ambiguous binding for parameter '{parameter.Name}'. Both context and services can supply '{parameter.ParameterType.Name}'. Use [FromContext] or [FromServices].");
		}

		if (foundContext)
		{
			resolved = contextValue;
			return true;
		}

		if (serviceValue is not null)
		{
			resolved = serviceValue;
			return true;
		}

		resolved = null;
		return false;
	}

	private static bool TryResolveAllFromContext(
		Type parameterType,
		IReadOnlyList<object?> contextValues,
		out object? value)
	{
		if (!TryGetCollectionElementType(parameterType, out var collectionElementType))
		{
			throw new InvalidOperationException(
				$"[FromContext(All = true)] requires a collection parameter type. '{parameterType.Name}' is not supported.");
		}

		var matches = contextValues
			.Where(item => item is not null && collectionElementType.IsInstanceOfType(item))
			.Reverse()
			.ToArray();
		value = ConvertMatchesToTargetCollection(parameterType, collectionElementType, matches);
		return true;
	}

	private static object? ResolveService(Type parameterType, IServiceProvider serviceProvider, string? key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return serviceProvider.GetService(parameterType);
		}

		return TryGetKeyedService(serviceProvider, parameterType, key);
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Optional keyed-service lookup is best-effort and only used when DI abstractions are present.")]
	private static object? TryGetKeyedService(IServiceProvider serviceProvider, Type parameterType, string key)
	{
		const string keyedProviderInterface = "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider";
		var providerInterface = serviceProvider.GetType().GetInterface(keyedProviderInterface, ignoreCase: false);
		if (providerInterface is null)
		{
			return null;
		}

		var getKeyedService = providerInterface.GetMethod("GetKeyedService", [typeof(Type), typeof(object)]);
		if (getKeyedService is null)
		{
			return null;
		}

		return getKeyedService.Invoke(serviceProvider, [parameterType, key]);
	}

	private static bool TryResolveFromContext(
		Type parameterType,
		IReadOnlyList<object?> contextValues,
		out object? value)
	{
		if (TryGetCollectionElementType(parameterType, out var collectionElementType))
		{
			var matches = contextValues
				.Where(item => item is not null && collectionElementType.IsInstanceOfType(item))
				.Reverse()
				.ToArray();
			if (matches.Length == 0)
			{
				value = null;
				return false;
			}

			value = ConvertMatchesToTargetCollection(parameterType, collectionElementType, matches);
			return true;
		}

		for (var index = contextValues.Count - 1; index >= 0; index--)
		{
			var candidate = contextValues[index];
			if (candidate is not null && parameterType.IsInstanceOfType(candidate))
			{
				value = candidate;
				return true;
			}
		}

		value = null;
		return false;
	}

	private static bool TryConsumePositional(
		System.Reflection.ParameterInfo parameter,
		IReadOnlyList<string> positionalArguments,
		IFormatProvider numericFormatProvider,
		bool enumIgnoreCase,
		ref int positionalIndex,
		out object? value)
	{
		var targetType = parameter.ParameterType;
		if (TryGetCollectionElementType(targetType, out var elementType))
		{
			var remaining = positionalArguments.Skip(positionalIndex).ToArray();
			if (remaining.Length == 0)
			{
				value = null;
				return false;
			}

			positionalIndex = positionalArguments.Count;
			value = ConvertMany(remaining, targetType, elementType, numericFormatProvider, enumIgnoreCase);
			return true;
		}

		if (positionalIndex >= positionalArguments.Count)
		{
			value = null;
			return false;
		}

		value = ParameterValueConverter.ConvertSingle(
			positionalArguments[positionalIndex],
			targetType,
			numericFormatProvider,
			enumIgnoreCase);
		positionalIndex++;
		return true;
	}

	private static object? ConvertManyOrSingle(
		System.Reflection.ParameterInfo parameter,
		IReadOnlyList<string> values,
		Type targetType,
		IFormatProvider numericFormatProvider,
		bool enumIgnoreCase)
	{
		if (!TryGetCollectionElementType(targetType, out var elementType))
		{
			if (values.Count > 1)
			{
				throw new InvalidOperationException(
					$"Parameter '{parameter.Name}' received multiple values but does not support repeated occurrences.");
			}

			return ParameterValueConverter.ConvertSingle(
				values.Count == 0 ? null : values[0],
				targetType,
				numericFormatProvider,
				enumIgnoreCase);
		}

		return ConvertMany(values, targetType, elementType, numericFormatProvider, enumIgnoreCase);
	}

	private static object ConvertMany(
		IEnumerable<string> values,
		Type targetType,
		Type elementType,
		IFormatProvider numericFormatProvider,
		bool enumIgnoreCase)
	{
		var converted = values
			.Select(value => ParameterValueConverter.ConvertSingle(value, elementType, numericFormatProvider, enumIgnoreCase))
			.ToArray();

		if (targetType.IsArray)
		{
			var array = Array.CreateInstance(elementType, converted.Length);
			for (var index = 0; index < converted.Length; index++)
			{
				array.SetValue(converted[index], index);
			}

			return array;
		}

		var listType = typeof(List<>).MakeGenericType(elementType);
		var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
		foreach (var item in converted)
		{
			list.Add(item);
		}

		return list;
	}

	private static bool CanConsumePositional(
		System.Reflection.ParameterInfo parameter,
		IReadOnlyList<string> positionalArguments,
		int positionalIndex)
	{
		if (TryGetCollectionElementType(parameter.ParameterType, out _))
		{
			return positionalIndex < positionalArguments.Count;
		}

		return positionalIndex < positionalArguments.Count;
	}

	private static ReplParameterMode ResolveBindingMode(
		System.Reflection.ParameterInfo parameter,
		OptionSchema optionSchema)
	{
		if (!string.IsNullOrWhiteSpace(parameter.Name)
			&& optionSchema.TryGetParameter(parameter.Name!, out var schemaParameter))
		{
			return schemaParameter.Mode;
		}

		var optionAttribute = parameter.GetCustomAttributes(typeof(ReplOptionAttribute), inherit: true)
			.Cast<ReplOptionAttribute>()
			.SingleOrDefault();
		if (optionAttribute is not null)
		{
			return optionAttribute.Mode;
		}

		var argumentAttribute = parameter.GetCustomAttributes(typeof(ReplArgumentAttribute), inherit: true)
			.Cast<ReplArgumentAttribute>()
			.SingleOrDefault();
		return argumentAttribute?.Mode ?? ReplParameterMode.OptionAndPositional;
	}

	private static bool ResolveEnumIgnoreCase(string parameterName, InvocationBindingContext context)
	{
		if (!context.OptionSchema.TryGetParameter(parameterName, out var schemaParameter))
		{
			return context.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive;
		}

		var effectiveCaseSensitivity = schemaParameter.CaseSensitivity ?? context.OptionCaseSensitivity;
		return effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive;
	}

	private static bool TryGetCollectionElementType(Type parameterType, out Type elementType)
	{
		if (parameterType.IsArray)
		{
			elementType = parameterType.GetElementType()!;
			return true;
		}

		if (parameterType.IsGenericType
			&& parameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
		{
			elementType = parameterType.GetGenericArguments()[0];
			return true;
		}

		if (parameterType.IsGenericType
			&& parameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
		{
			elementType = parameterType.GetGenericArguments()[0];
			return true;
		}

		if (parameterType.IsGenericType
			&& parameterType.GetGenericTypeDefinition() == typeof(List<>))
		{
			elementType = parameterType.GetGenericArguments()[0];
			return true;
		}

		elementType = typeof(void);
		return false;
	}

	private static object ConvertMatchesToTargetCollection(
		Type targetType,
		Type elementType,
		object?[] matches)
	{
		if (targetType.IsArray)
		{
			var array = Array.CreateInstance(elementType, matches.Length);
			for (var index = 0; index < matches.Length; index++)
			{
				array.SetValue(matches[index], index);
			}

			return array;
		}

		var listType = typeof(List<>).MakeGenericType(elementType);
		var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
		foreach (var match in matches)
		{
			list.Add(match);
		}

		return list;
	}
}
