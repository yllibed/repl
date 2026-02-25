namespace Repl;

/// <summary>
/// Provides typed context helpers that preserve the current app interface in fluent chains.
/// </summary>
public static class CoreReplAppContextExtensions
{
	/// <summary>
	/// Creates a context segment while preserving the app interface type for nested configuration.
	/// </summary>
	/// <typeparam name="TApp">App interface type.</typeparam>
	/// <param name="app">Target app.</param>
	/// <param name="segment">Context segment template.</param>
	/// <param name="configure">Nested configuration callback.</param>
	/// <param name="validation">Optional validator for scope entry.</param>
	/// <returns>The same app instance.</returns>
	public static TApp Context<TApp>(
		this TApp app,
		string segment,
		Action<TApp> configure,
		Delegate? validation = null)
		where TApp : ICoreReplApp
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(configure);

		app.Context(
			segment,
			scoped =>
			{
				if (scoped is not TApp typed)
				{
					throw new InvalidOperationException(
						$"Unable to preserve app type '{typeof(TApp).Name}' in nested context '{segment}'. " +
						"Use interface-typed app variables (ICoreReplApp/IReplApp) for typed Context chaining.");
				}

				configure(typed);
			},
			validation);

		return app;
	}
}
