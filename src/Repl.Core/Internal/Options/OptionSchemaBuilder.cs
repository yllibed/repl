using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Repl;

namespace Repl.Internal.Options;

internal static class OptionSchemaBuilder
{
	public static OptionSchema Build(
		RouteTemplate template,
		CommandBuilder command,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(template);
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var routeParameterNames = template.Segments
			.OfType<DynamicRouteSegment>()
			.Select(segment => segment.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var entries = new List<OptionSchemaEntry>();
		var parameters = new Dictionary<string, OptionSchemaParameter>(StringComparer.OrdinalIgnoreCase);
		foreach (var parameter in command.Handler.Method.GetParameters())
		{
			if (ShouldSkipSchemaParameter(parameter, routeParameterNames))
			{
				continue;
			}

#pragma warning disable IL2072
			if (IsOptionsGroupParameter(parameter))
			{
				AppendOptionsGroupSchemaEntries(parameter.ParameterType, entries, parameters);
			}
#pragma warning restore IL2072
			else
			{
				AppendParameterSchemaEntries(parameter, entries, parameters);
			}
		}

		ValidateTokenCollisions(entries, parsingOptions);
		return new OptionSchema(entries, parameters);
	}

	private static bool ShouldSkipSchemaParameter(
		ParameterInfo parameter,
		HashSet<string> routeParameterNames)
	{
		if (string.IsNullOrWhiteSpace(parameter.Name)
			|| parameter.ParameterType == typeof(CancellationToken)
			|| IsFrameworkInjectedParameter(parameter)
			|| parameter.GetCustomAttribute<FromContextAttribute>() is not null
			|| parameter.GetCustomAttribute<FromServicesAttribute>() is not null)
		{
			return true;
		}

		if (!routeParameterNames.Contains(parameter.Name))
		{
			return false;
		}

		var optionAttribute = parameter.GetCustomAttribute<ReplOptionAttribute>(inherit: true);
		var argumentAttribute = parameter.GetCustomAttribute<ReplArgumentAttribute>(inherit: true);
		if (optionAttribute is null && argumentAttribute is null)
		{
			return true;
		}

		throw new InvalidOperationException(
			$"Route parameter '{parameter.Name}' cannot declare ReplOption/ReplArgument attributes.");
	}

	private static void AppendParameterSchemaEntries(
		ParameterInfo parameter,
		List<OptionSchemaEntry> entries,
		Dictionary<string, OptionSchemaParameter> parameters)
	{
		var optionAttribute = parameter.GetCustomAttribute<ReplOptionAttribute>(inherit: true);
		var argumentAttribute = parameter.GetCustomAttribute<ReplArgumentAttribute>(inherit: true);
		var mode = optionAttribute?.Mode
			?? argumentAttribute?.Mode
			?? ReplParameterMode.OptionAndPositional;
		if (parameters.ContainsKey(parameter.Name!))
		{
			throw new InvalidOperationException(
				$"Option token collision detected for parameter name '{parameter.Name}'.");
		}

		parameters[parameter.Name!] = new OptionSchemaParameter(
			parameter.Name!,
			parameter.ParameterType,
			mode,
			CaseSensitivity: optionAttribute?.CaseSensitivity);
		if (mode == ReplParameterMode.ArgumentOnly)
		{
			return;
		}

		var arity = ResolveArity(parameter, optionAttribute);
		var tokenKind = ResolveTokenKind(parameter.ParameterType, arity);
		var canonicalToken = ResolveCanonicalToken(parameter.Name!, optionAttribute);
		entries.Add(new OptionSchemaEntry(
			canonicalToken,
			parameter.Name!,
			tokenKind,
			arity,
			CaseSensitivity: optionAttribute?.CaseSensitivity));
		AppendOptionAliases(parameter, tokenKind, arity, optionAttribute, entries);
		AppendReverseAliases(parameter, optionAttribute, entries);
		AppendValueAliases(parameter, optionAttribute, entries);
		AppendEnumAliases(parameter, optionAttribute, entries);
	}

	private static OptionSchemaTokenKind ResolveTokenKind(Type parameterType, ReplArity arity) =>
		IsBoolParameter(parameterType) && arity != ReplArity.ExactlyOne
			? OptionSchemaTokenKind.BoolFlag
			: OptionSchemaTokenKind.NamedOption;

	private static string ResolveCanonicalToken(string parameterName, ReplOptionAttribute? optionAttribute)
	{
		var canonicalName = string.IsNullOrWhiteSpace(optionAttribute?.Name)
			? parameterName
			: optionAttribute!.Name!;
		return EnsureLongPrefix(canonicalName);
	}

	private static void AppendOptionAliases(
		ParameterInfo parameter,
		OptionSchemaTokenKind tokenKind,
		ReplArity arity,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var alias in optionAttribute?.Aliases ?? [])
		{
			ValidateOptionToken(alias, parameter.Name!);
			entries.Add(new OptionSchemaEntry(
				alias,
				parameter.Name!,
				tokenKind,
				arity,
				CaseSensitivity: optionAttribute?.CaseSensitivity));
		}
	}

	private static void AppendReverseAliases(
		ParameterInfo parameter,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var reverseAlias in optionAttribute?.ReverseAliases ?? [])
		{
			ValidateOptionToken(reverseAlias, parameter.Name!);
			entries.Add(new OptionSchemaEntry(
				reverseAlias,
				parameter.Name!,
				OptionSchemaTokenKind.ReverseFlag,
				ReplArity.ZeroOrOne,
				CaseSensitivity: optionAttribute?.CaseSensitivity,
				InjectedValue: "false"));
		}
	}

	private static void AppendValueAliases(
		ParameterInfo parameter,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var valueAlias in parameter.GetCustomAttributes<ReplValueAliasAttribute>(inherit: true))
		{
			ValidateOptionToken(valueAlias.Token, parameter.Name!);
			entries.Add(new OptionSchemaEntry(
				valueAlias.Token,
				parameter.Name!,
				OptionSchemaTokenKind.ValueAlias,
				ReplArity.ZeroOrOne,
				CaseSensitivity: valueAlias.CaseSensitivity ?? optionAttribute?.CaseSensitivity,
				InjectedValue: valueAlias.Value));
		}
	}

	private static bool IsFrameworkInjectedParameter(ParameterInfo parameter) =>
		parameter.ParameterType == typeof(IServiceProvider)
		|| parameter.ParameterType == typeof(ICoreReplApp)
		|| parameter.ParameterType == typeof(CoreReplApp)
		|| parameter.ParameterType == typeof(IReplSessionState)
		|| parameter.ParameterType == typeof(IReplInteractionChannel)
		|| parameter.ParameterType == typeof(IReplIoContext)
		|| parameter.ParameterType == typeof(IReplKeyReader);

	private static ReplArity ResolveArity(ParameterInfo parameter, ReplOptionAttribute? optionAttribute)
	{
		if (optionAttribute?.Arity is { } explicitArity)
		{
			return explicitArity;
		}

		if (IsBoolParameter(parameter.ParameterType))
		{
			return ReplArity.ZeroOrOne;
		}

		if (IsCollection(parameter.ParameterType))
		{
			return ReplArity.ZeroOrMore;
		}

		if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null || parameter.HasDefaultValue)
		{
			return ReplArity.ZeroOrOne;
		}

		return ReplArity.ExactlyOne;
	}

	private static bool IsCollection(Type type) =>
		type.IsArray
		|| (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
		|| (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));

	private static bool IsBoolParameter(Type type) =>
		(Nullable.GetUnderlyingType(type) ?? type) == typeof(bool);

	private static string ToCamelCase(string name) =>
		name.Length == 0 || char.IsLower(name[0])
			? name
			: string.Create(name.Length, name, static (span, source) =>
			{
				source.AsSpan().CopyTo(span);
				span[0] = char.ToLowerInvariant(span[0]);
			});

	private static string EnsureLongPrefix(string name) =>
		name.StartsWith("--", StringComparison.Ordinal) ? name : $"--{name}";

	private static void ValidateOptionToken(string token, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(token)
			|| token.Contains(' ', StringComparison.Ordinal)
			|| (!token.StartsWith("--", StringComparison.Ordinal) && !token.StartsWith('-')))
		{
			throw new InvalidOperationException(
				$"Invalid option token '{token}' declared on parameter '{parameterName}'.");
		}
	}

	private static void AppendEnumAliases(
		ParameterInfo parameter,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		var enumType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!enumType.IsEnum)
		{
			return;
		}

#pragma warning disable IL2075
		foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
#pragma warning restore IL2075
		{
			var enumFlag = field.GetCustomAttribute<ReplEnumFlagAttribute>(inherit: false);
			if (enumFlag is null)
			{
				continue;
			}

			foreach (var alias in enumFlag.Aliases)
			{
				ValidateOptionToken(alias, parameter.Name ?? enumType.Name);
				entries.Add(new OptionSchemaEntry(
					alias,
					parameter.Name!,
					OptionSchemaTokenKind.EnumAlias,
					ReplArity.ZeroOrOne,
					CaseSensitivity: enumFlag.CaseSensitivity ?? optionAttribute?.CaseSensitivity,
					InjectedValue: field.Name));
			}
		}
	}

	private static bool IsOptionsGroupParameter(ParameterInfo parameter) =>
		Attribute.IsDefined(parameter.ParameterType, typeof(ReplOptionsGroupAttribute), inherit: true);

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Options group types are user-defined and always preserved by the handler delegate reference.")]
	private static void AppendOptionsGroupSchemaEntries(
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		Type groupType,
		List<OptionSchemaEntry> entries,
		Dictionary<string, OptionSchemaParameter> parameters)
	{
		ValidateOptionsGroupType(groupType);

		foreach (var property in groupType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (!property.CanWrite)
			{
				continue;
			}

			if (Attribute.IsDefined(property.PropertyType, typeof(ReplOptionsGroupAttribute), inherit: true))
			{
				throw new InvalidOperationException(
					$"Nested options groups are not supported. Property '{property.Name}' on '{groupType.Name}' is itself an options group.");
			}

			AppendPropertySchemaEntries(property, entries, parameters);
		}
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Options group types are user-defined and always preserved by the handler delegate reference.")]
	private static void ValidateOptionsGroupType(
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		Type groupType)
	{
		if (groupType.IsAbstract || groupType.IsInterface)
		{
			throw new InvalidOperationException(
				$"Options group type '{groupType.Name}' must be a concrete class.");
		}

		if (groupType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null)
		{
			throw new InvalidOperationException(
				$"Options group type '{groupType.Name}' must have a public parameterless constructor.");
		}
	}

	private static void AppendPropertySchemaEntries(
		PropertyInfo property,
		List<OptionSchemaEntry> entries,
		Dictionary<string, OptionSchemaParameter> parameters)
	{
		var optionAttribute = property.GetCustomAttribute<ReplOptionAttribute>(inherit: true);
		var argumentAttribute = property.GetCustomAttribute<ReplArgumentAttribute>(inherit: true);
		var mode = optionAttribute?.Mode
			?? argumentAttribute?.Mode
			?? ReplParameterMode.OptionAndPositional;
		if (parameters.ContainsKey(property.Name))
		{
			throw new InvalidOperationException(
				$"Option token collision detected for parameter name '{property.Name}'.");
		}

		parameters[property.Name] = new OptionSchemaParameter(
			property.Name,
			property.PropertyType,
			mode,
			CaseSensitivity: optionAttribute?.CaseSensitivity);
		if (mode == ReplParameterMode.ArgumentOnly)
		{
			return;
		}

		var arity = ResolvePropertyArity(property.PropertyType, optionAttribute);
		var tokenKind = ResolveTokenKind(property.PropertyType, arity);
		var canonicalToken = ResolveCanonicalToken(ToCamelCase(property.Name), optionAttribute);
		entries.Add(new OptionSchemaEntry(
			canonicalToken,
			property.Name,
			tokenKind,
			arity,
			CaseSensitivity: optionAttribute?.CaseSensitivity));
		AppendPropertyOptionAliases(property.Name, tokenKind, arity, optionAttribute, entries);
		AppendPropertyReverseAliases(property.Name, optionAttribute, entries);
		AppendPropertyValueAliases(property, optionAttribute, entries);
		AppendPropertyEnumAliases(property, optionAttribute, entries);
	}

	private static ReplArity ResolvePropertyArity(Type propertyType, ReplOptionAttribute? optionAttribute)
	{
		if (optionAttribute?.Arity is { } explicitArity)
		{
			return explicitArity;
		}

		if (IsBoolParameter(propertyType))
		{
			return ReplArity.ZeroOrOne;
		}

		if (IsCollection(propertyType))
		{
			return ReplArity.ZeroOrMore;
		}

		return ReplArity.ZeroOrOne;
	}

	private static void AppendPropertyOptionAliases(
		string propertyName,
		OptionSchemaTokenKind tokenKind,
		ReplArity arity,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var alias in optionAttribute?.Aliases ?? [])
		{
			ValidateOptionToken(alias, propertyName);
			entries.Add(new OptionSchemaEntry(
				alias,
				propertyName,
				tokenKind,
				arity,
				CaseSensitivity: optionAttribute?.CaseSensitivity));
		}
	}

	private static void AppendPropertyReverseAliases(
		string propertyName,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var reverseAlias in optionAttribute?.ReverseAliases ?? [])
		{
			ValidateOptionToken(reverseAlias, propertyName);
			entries.Add(new OptionSchemaEntry(
				reverseAlias,
				propertyName,
				OptionSchemaTokenKind.ReverseFlag,
				ReplArity.ZeroOrOne,
				CaseSensitivity: optionAttribute?.CaseSensitivity,
				InjectedValue: "false"));
		}
	}

	private static void AppendPropertyValueAliases(
		PropertyInfo property,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		foreach (var valueAlias in property.GetCustomAttributes<ReplValueAliasAttribute>(inherit: true))
		{
			ValidateOptionToken(valueAlias.Token, property.Name);
			entries.Add(new OptionSchemaEntry(
				valueAlias.Token,
				property.Name,
				OptionSchemaTokenKind.ValueAlias,
				ReplArity.ZeroOrOne,
				CaseSensitivity: valueAlias.CaseSensitivity ?? optionAttribute?.CaseSensitivity,
				InjectedValue: valueAlias.Value));
		}
	}

	private static void AppendPropertyEnumAliases(
		PropertyInfo property,
		ReplOptionAttribute? optionAttribute,
		List<OptionSchemaEntry> entries)
	{
		var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		if (!enumType.IsEnum)
		{
			return;
		}

#pragma warning disable IL2075
		foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
#pragma warning restore IL2075
		{
			var enumFlag = field.GetCustomAttribute<ReplEnumFlagAttribute>(inherit: false);
			if (enumFlag is null)
			{
				continue;
			}

			foreach (var alias in enumFlag.Aliases)
			{
				ValidateOptionToken(alias, property.Name);
				entries.Add(new OptionSchemaEntry(
					alias,
					property.Name,
					OptionSchemaTokenKind.EnumAlias,
					ReplArity.ZeroOrOne,
					CaseSensitivity: enumFlag.CaseSensitivity ?? optionAttribute?.CaseSensitivity,
					InjectedValue: field.Name));
			}
		}
	}

	private static void ValidateTokenCollisions(
		IReadOnlyList<OptionSchemaEntry> entries,
		ParsingOptions parsingOptions)
	{
		var comparer = parsingOptions.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var map = new Dictionary<string, OptionSchemaEntry>(comparer);
		foreach (var entry in entries)
		{
			if (!map.TryGetValue(entry.Token, out var existing))
			{
				map[entry.Token] = entry;
				continue;
			}

			if (string.Equals(existing.ParameterName, entry.ParameterName, StringComparison.OrdinalIgnoreCase)
				&& existing.TokenKind == entry.TokenKind
				&& string.Equals(existing.InjectedValue, entry.InjectedValue, StringComparison.Ordinal))
			{
				continue;
			}

			throw new InvalidOperationException(
				$"Option token collision detected for '{entry.Token}' between '{existing.ParameterName}' and '{entry.ParameterName}'.");
		}
	}
}
