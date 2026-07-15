using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Repl.Parameters;

namespace Repl;

/// <summary>
/// Extension methods for registering typed global options on <see cref="ReplApp"/>.
/// </summary>
public static class GlobalOptionsExtensions
{
	/// <summary>
	/// Registers a typed global options class. Public settable properties are registered as
	/// global options and the class itself is available via DI, populated from parsed values.
	/// </summary>
	/// <typeparam name="T">Options class with a parameterless constructor.</typeparam>
	/// <param name="app">The REPL application.</param>
	/// <returns>The application for fluent chaining.</returns>
	public static ReplApp UseGlobalOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(this ReplApp app)
		where T : class, new()
	{
		ArgumentNullException.ThrowIfNull(app);

		var parsing = app.Core.OptionsSnapshot.Parsing;
		var properties = GetOptionProperties<T>();

		ThrowIfUnsupportedOverrides(typeof(T), properties);

		app.Options(options =>
		{
			var prototype = new T();
			foreach (var property in properties)
			{
				var optionAttr = property.GetCustomAttribute<ReplOptionAttribute>();
				var name = optionAttr?.Name ?? ToKebabCase(property.Name);
				var aliases = optionAttr?.Aliases;
				// The effective default of a typed global option is always the prototype value:
				// PopulateInstance starts from new T() and only overwrites parsed values. Keep
				// the metadata aligned (even when the value equals the CLR default) so
				// IGlobalOptionsAccessor.GetValue resolves the same default as the injected T.
				var defaultValue = property.GetValue(prototype)?.ToString();
				var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;

				options.Parsing.AddGlobalOptionCore(name, property.PropertyType, aliases, defaultValue, description, typeof(T));
			}
		});

		app.Core.RegisterGlobalOptionsType(typeof(T));
		app.ServiceDescriptors.TryAddTransient(sp =>
		{
			var accessor = sp.GetRequiredService<IGlobalOptionsAccessor>();
			return PopulateInstance<T>(accessor, parsing.NumericFormatProvider);
		});

		return app;
	}

	// Typed global options do not flow per-option CaseSensitivity/Arity overrides into
	// AddGlobalOptionCore (the global-option pipeline has no per-option override concept
	// yet). Fail fast instead of silently discarding a now-settable override. Enum-flag
	// overrides are declared on the enum TYPE's fields — which may be legitimately shared
	// with route commands where they do work — so they are deliberately not rejected here.
	private static void ThrowIfUnsupportedOverrides(Type optionsType, IReadOnlyList<PropertyInfo> properties)
	{
		foreach (var property in properties)
		{
			var optionAttr = property.GetCustomAttribute<ReplOptionAttribute>();
			if (optionAttr?.CaseSensitivityOverride is not null)
			{
				throw new NotSupportedException(
					$"Global option property '{optionsType.Name}.{property.Name}' declares a CaseSensitivity override, which is not supported for typed global options. Remove the override or expose the option through a per-command options type.");
			}

			if (optionAttr?.ArityOverride is not null)
			{
				throw new NotSupportedException(
					$"Global option property '{optionsType.Name}.{property.Name}' declares an Arity override, which is not supported for typed global options. Remove the override or expose the option through a per-command options type.");
			}

			foreach (var valueAlias in property.GetCustomAttributes<ReplValueAliasAttribute>())
			{
				if (valueAlias.CaseSensitivityOverride is not null)
				{
					throw new NotSupportedException(
						$"Global option property '{optionsType.Name}.{property.Name}' declares a CaseSensitivity override on value alias '{valueAlias.Token}', which is not supported for typed global options. Remove the override or expose the option through a per-command options type.");
				}
			}
		}
	}

	internal static T PopulateInstance<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(IGlobalOptionsAccessor accessor, IFormatProvider numericFormatProvider)
		where T : class, new()
	{
		var instance = new T();
		foreach (var property in GetOptionProperties<T>())
		{
			var optionAttr = property.GetCustomAttribute<ReplOptionAttribute>();
			var name = optionAttr?.Name ?? ToKebabCase(property.Name);

			var rawValues = accessor.GetRawValues(name);
			if (rawValues.Count == 0)
			{
				continue;
			}

			var value = ParameterValueConverter.ConvertSingle(
				rawValues[0],
				property.PropertyType,
				numericFormatProvider);
			property.SetValue(instance, value);
		}

		return instance;
	}

	private static IReadOnlyList<PropertyInfo> GetOptionProperties<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
		GlobalOptionsMetadata<T>.Properties;

	private static class GlobalOptionsMetadata<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
	{
		internal static readonly IReadOnlyList<PropertyInfo> Properties =
			typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p.CanWrite)
				.ToArray();
	}

	private static string ToKebabCase(string pascalCase)
	{
		if (string.IsNullOrEmpty(pascalCase))
		{
			return pascalCase;
		}

		var builder = new System.Text.StringBuilder(pascalCase.Length + 4);
		for (var i = 0; i < pascalCase.Length; i++)
		{
			var c = pascalCase[i];
			if (char.IsUpper(c) && i > 0)
			{
				// Only insert hyphen at the start of an uppercase run or
				// at the transition from an uppercase run to a lowercase char.
				// "XMLPort" → "xml-port", "MaxRetries" → "max-retries"
				var prevIsUpper = char.IsUpper(pascalCase[i - 1]);
				var nextIsLower = i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]);
				if (!prevIsUpper || nextIsLower)
				{
					builder.Append('-');
				}
			}

			builder.Append(char.ToLowerInvariant(c));
		}

		return builder.ToString();
	}
}
