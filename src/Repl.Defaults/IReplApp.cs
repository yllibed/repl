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
	/// <returns>A context builder for context-level metadata configuration.</returns>
	IContextBuilder Context(string segment, Action<IReplApp> configure, Delegate? validation = null);

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
	/// Maps a reusable module instance into the current route scope with a runtime presence predicate.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <param name="isPresent">Runtime presence predicate.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new IReplApp MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent);

	/// <summary>
	/// Maps a reusable module instance into the current route scope with an injectable runtime presence predicate.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <param name="isPresent">
	/// Predicate delegate that must return <see langword="bool"/>. Parameters are resolved from defaults services,
	/// with special handling for <see cref="ModulePresenceContext"/>, <see cref="ReplRuntimeChannel"/>,
	/// <see cref="IReplSessionState"/>, and <see cref="IReplSessionInfo"/>.
	/// </param>
	/// <returns>The same app contract for fluent chaining.</returns>
	IReplApp MapModule(IReplModule module, Delegate isPresent);

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
