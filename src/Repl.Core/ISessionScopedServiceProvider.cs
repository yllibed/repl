namespace Repl;

/// <summary>
/// Marks a service provider that already represents a single session's DI scope.
/// </summary>
/// <remarks>
/// The Run* entry points open one Microsoft DI <c>AsyncServiceScope</c>
/// per session so Scoped services resolve per session. A session owner that spans several
/// Run* calls with one provider (e.g. Repl.Testing's session handle, which executes each
/// command as a separate one-shot run) owns its scope itself and passes a provider marked
/// with this interface — the entry points then skip scoping instead of creating a fresh
/// scope (and fresh Scoped instances) per call.
/// </remarks>
internal interface ISessionScopedServiceProvider : IServiceProvider;
