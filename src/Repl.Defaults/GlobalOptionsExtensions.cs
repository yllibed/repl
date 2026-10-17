using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Repl;

/// <summary>Registers typed global options with the DI-enabled facade.</summary>
public static class GlobalOptionsExtensions
{
	/// <summary>Declares typed global options and registers their DI factory.</summary>
	/// <typeparam name="T">Options class with a public parameterless constructor.</typeparam>
	/// <param name="app">Application to configure.</param>
	/// <returns>The application.</returns>
	public static ReplApp UseGlobalOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(this ReplApp app)
		where T : class, new()
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseGlobalOptions<T>();
		app.ServiceDescriptors.TryAddTransient(sp => CoreGlobalOptionsExtensions.PopulateInstance<T>(
			sp.GetRequiredService<IGlobalOptionsAccessor>(), app.Core.OptionsSnapshot.Parsing.NumericFormatProvider));
		return app;
	}
}
