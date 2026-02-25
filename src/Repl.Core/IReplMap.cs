namespace Repl;

/// <summary>
/// Defines the command and context mapping contract used by the REPL graph.
/// </summary>
public interface IReplMap
{
	/// <summary>
	/// Maps a terminal route to a handler delegate.
	/// </summary>
	/// <param name="route">Route template to register.</param>
	/// <param name="handler">Handler delegate to execute.</param>
	/// <returns>A command builder for metadata configuration.</returns>
	CommandBuilder Map(string route, Delegate handler);

	/// <summary>
	/// Creates a context segment and configures child routes within it.
	/// </summary>
	/// <param name="segment">Context segment template.</param>
	/// <param name="configure">Mapping callback for nested routes.</param>
	/// <param name="validation">Optional validator for scope entry.</param>
	/// <returns>The same mapper for fluent chaining.</returns>
	IReplMap Context(string segment, Action<IReplMap> configure, Delegate? validation = null);

	/// <summary>
	/// Maps a reusable module instance into the current route scope.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <returns>The same mapper for fluent chaining.</returns>
	IReplMap MapModule(IReplModule module);

	/// <summary>
	/// Registers a banner delegate displayed when entering this scope in interactive mode.
	/// Unlike <c>WithDescription</c>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="bannerProvider">Banner delegate with injectable parameters.</param>
	/// <returns>The same mapper for fluent chaining.</returns>
	IReplMap WithBanner(Delegate bannerProvider);

	/// <summary>
	/// Registers a static banner string displayed when entering this scope in interactive mode.
	/// Unlike <c>WithDescription</c>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="text">Banner text.</param>
	/// <returns>The same mapper for fluent chaining.</returns>
	IReplMap WithBanner(string text);
}
