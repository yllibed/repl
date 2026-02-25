namespace Repl;

/// <summary>
/// Core app contract for mapping commands, contexts, and modules without DI dependencies.
/// </summary>
public interface ICoreReplApp : IReplMap
{
	/// <summary>
	/// Creates a context segment and configures child routes within it.
	/// </summary>
	/// <param name="segment">Context segment template.</param>
	/// <param name="configure">Mapping callback for nested routes.</param>
	/// <param name="validation">Optional validator for scope entry.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	ICoreReplApp Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null);

	/// <summary>
	/// Maps a reusable module instance into the current route scope.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new ICoreReplApp MapModule(IReplModule module);

	/// <summary>
	/// Registers a banner delegate rendered when the scope is entered.
	/// </summary>
	/// <param name="bannerProvider">Banner delegate with injectable parameters.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new ICoreReplApp WithBanner(Delegate bannerProvider);

	/// <summary>
	/// Registers a static banner string rendered when the scope is entered.
	/// </summary>
	/// <param name="text">Banner text.</param>
	/// <returns>The same app contract for fluent chaining.</returns>
	new ICoreReplApp WithBanner(string text);
}
