namespace Repl;

/// <summary>
/// DI-enabled app contract for mapping commands, contexts, and modules.
/// </summary>
public interface IReplApp : ICoreReplApp
{
	/// <summary>
	/// Creates a context segment and configures child routes within it.
	/// </summary>
	/// <param name="segment">Context segment template.</param>
	/// <param name="configure">Mapping callback for nested routes.</param>
	/// <param name="validation">Optional validator for scope entry.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	IReplApp Context(string segment, Action<IReplApp> configure, Delegate? validation = null);

	/// <summary>
	/// Maps a module resolved through DI activation.
	/// </summary>
	/// <typeparam name="TModule">Module type.</typeparam>
	/// <returns>The same app contract for fluent chaining.</returns>
	IReplApp MapModule<TModule>()
		where TModule : class, IReplModule;

	/// <summary>
	/// Maps a reusable module instance into the current route scope.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new IReplApp MapModule(IReplModule module);

	/// <summary>
	/// Registers a banner delegate rendered when the scope is entered.
	/// </summary>
	/// <param name="bannerProvider">Banner delegate with injectable parameters.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new IReplApp WithBanner(Delegate bannerProvider);

	/// <summary>
	/// Registers a static banner string rendered when the scope is entered.
	/// </summary>
	/// <param name="text">Banner text.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new IReplApp WithBanner(string text);
}
