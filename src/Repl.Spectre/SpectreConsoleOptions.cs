using System.Diagnostics.CodeAnalysis;

namespace Repl.Spectre;

/// <summary>
/// Configuration options for the Spectre.Console integration.
/// </summary>
public sealed class SpectreConsoleOptions
{
	private readonly Dictionary<Type, Func<object, IRenderable>> _views = [];

	/// <summary>
	/// Registers a Spectre view for an exact runtime type. Registering the same type again replaces its view.
	/// </summary>
	/// <typeparam name="T">The command result type.</typeparam>
	/// <param name="view">Builds a renderable without writing to a console.</param>
	/// <remarks>
	/// Views affect only Spectre output. Help, result envelopes and pages retain their built-in rendering;
	/// page items and nested object properties are not individually passed through registered views.
	/// Configure views at startup, before running the application.
	/// </remarks>
	public void AddView<T>(Func<T, IRenderable> view)
	{
		ArgumentNullException.ThrowIfNull(view);
		_views[typeof(T)] = value => view((T)value)
			?? throw new InvalidOperationException($"The Spectre view for '{typeof(T)}' returned null.");
	}

	internal bool TryGetView(Type type, [NotNullWhen(true)] out Func<object, IRenderable>? view) => _views.TryGetValue(type, out view);

	/// <summary>
	/// Gets or sets a value indicating whether Unicode box-drawing characters
	/// and symbols are used. When <c>false</c>, Spectre falls back to ASCII.
	/// Default is <c>true</c>.
	/// </summary>
	public bool Unicode { get; set; } = true;
}
