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

		ParsingOptions? capturedParsing = null;
		app.Options(options =>
		{
			capturedParsing = options.Parsing;
			var prototype = new T();
			foreach (var property in GetOptionProperties<T>())
			{
				var optionAttr = property.GetCustomAttribute<ReplOptionAttribute>();
				var name = optionAttr?.Name ?? ToKebabCase(property.Name);
				var aliases = optionAttr?.Aliases;
				var defaultValue = property.GetValue(prototype)?.ToString();

				options.Parsing.AddGlobalOptionCore(name, property.PropertyType, aliases, defaultValue);
			}
		});

		app.ServiceDescriptors.TryAddTransient(sp =>
		{
			var accessor = sp.GetRequiredService<IGlobalOptionsAccessor>();
			return PopulateInstance<T>(accessor, capturedParsing!.NumericFormatProvider);
		});

		return app;
	}

	internal static T PopulateInstance<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(IGlobalOptionsAccessor accessor, IFormatProvider numericFormatProvider)
		where T : class, new()
	{
		var instance = new T();
		foreach (var property in GetOptionProperties<T>())
		{
			var optionAttr = property.GetCustomAttribute<ReplOptionAttribute>();
			var name = optionAttr?.Name ?? ToKebabCase(property.Name);

			if (!accessor.HasValue(name))
			{
				continue;
			}

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

	private static PropertyInfo[] GetOptionProperties<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
		typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanWrite)
			.ToArray();

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
