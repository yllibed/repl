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

			AppendParameterSchemaEntries(parameter, entries, parameters);
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
