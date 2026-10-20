# MCP Transports: Custom Transports and HTTP Integration

> **This page is for you if** you need WebSocket, named pipe, SSH, or HTTP transports instead of stdio.
>
> **Purpose:** Custom transport and HTTP hosting scenarios.
> **Prerequisite:** [MCP overview](mcp-overview.md)
> **Related:** [Reference](mcp-reference.md) · [Advanced patterns](mcp-advanced.md)

## Scenario A: Stdio-over-anything

The MCP protocol is JSON-RPC over stdin/stdout. The `TransportFactory` option lets you replace the physical transport while keeping the same protocol.

Use this for:

- WebSocket bridges
- Named pipes
- SSH tunnels
- Any other stream-based transport

```csharp
app.UseMcpServer(o =>
{
    o.TransportFactory = (serverName, io) =>
    {
        var (inputStream, outputStream) = CreateWebSocketBridge();
        return new StreamServerTransport(inputStream, outputStream, serverName);
    };
});
```

The app still launches via `myapp mcp serve`. This gives you one MCP session per process.

### Multi-session custom transports

If your transport accepts multiple concurrent connections, build `McpServerOptions` once and create a server per connection:

```csharp
var mcpOptions = app.Core.BuildMcpServerOptions();

async Task HandleConnectionAsync(Stream input, Stream output, CancellationToken ct)
{
    var transport = new StreamServerTransport(input, output, "my-server");
    var server = McpServer.Create(transport, mcpOptions);
    await server.RunAsync(ct);
    await server.DisposeAsync();
}
```

Client capabilities (sampling, elicitation, roots) resolve per **request** on this path, so each
connection sees its own — that is a property of the request, not of the options instance.

> **Known limitation:** cross-call state does not resolve per request, and three consequences are
> worth knowing before you choose this shape. The `2026-07-28` revision removed protocol-level
> sessions, so this path has no per-connection identity to hang state on.
>
> [Soft roots](mcp-advanced.md#soft-roots-fallback) set by one connection are visible to every other
> connection built from the same options — they are host-set state with no request to belong to. If
> your commands rely on them, host one server per process (`mcp serve`) or pass the workspace as an
> explicit command argument. Note that on `2026-07-28` soft roots never reveal commands on any
> transport: the advertised set must not change as a side effect of a `tools/call`. See
> [Conformance](mcp-conformance.md#tool-list-invariance-on-2026-07-28).
>
> The command catalog is frozen when `BuildMcpServerOptions()` returns. A server built from it never
> emits `*/list_changed`, even though the SDK advertises the capability, so a client that would
> refresh on that notification never does. Commands whose visibility changes at runtime — dynamic
> tools — need `mcp serve`. Presence predicates are a separate matter on `2026-07-28`: discovery
> resolves every per-connection question to a constant there, so the predicate's value under those
> constants decides presence once and for all. One that evaluates true is advertised to every client;
> one that evaluates false — a data gate, or a negated capability gate such as `!roots.IsSupported` —
> is advertised to none, and a frozen catalog has no later chance to change its mind. See
> [Conformance](mcp-conformance.md#what-this-means-when-you-write-commands).
>
> Native roots are safe here: they are resolved per request rather than cached per connection, so one
> client never sees another's workspace. The cost is one `roots/list` round-trip per request that asks
> for them. Under stateless HTTP the SDK reports no client capabilities at all, so native roots are
> unavailable on that transport whatever you do.

## Scenario B: MCP-over-HTTP

This advanced integration is for applications where Repl is one component of an ASP.NET Core host. The host and the MCP SDK own HTTP, request scopes, authentication, authorization and shutdown. Repl projects and executes the command graph.

Reference `Repl.Mcp` and `ModelContextProtocol.AspNetCore` (use the same MCP SDK version as `Repl.Mcp`). No additional Repl HTTP package is required.

```csharp
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using Repl;
using Repl.Mcp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRepl((_, repl) =>
{
    repl.UseEmbeddedConsoleProfile();
    repl.Map("status", () => "all systems go").ReadOnly();
    repl.Map("admin reset", () => "reset requested");
});

builder.Services.AddMcpServer().WithHttpTransport(http =>
{
    http.SessionMode = HttpServerSessionMode.Stateless;
    http.ConfigureSessionOptions = (context, target, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repl = context.RequestServices.GetRequiredService<ReplApp>();
        repl.ConfigureMcpServerOptions(target, options =>
        {
            options.ServerName = "MyApi";
            options.AutoPromoteReadOnlyToResources = false;
            options.CommandFilter = command =>
                string.Equals(command.Path, "status", StringComparison.Ordinal);
        }, context.RequestServices);
        return Task.CompletedTask;
    };
});

var webApp = builder.Build();
webApp.MapMcp("/mcp");
await webApp.RunAsync();
```

Only `status` is projected. `CommandFilter` controls Repl-generated primitives; it does not filter fallback handlers or independent tools the host adds after applying the projection. Primitive collections registered before the helper runs are replaced. Compose authentication and authorization on the SDK endpoint using the host's normal policies.

### Startup and request order

1. Register business services in `builder.Services` and declare the Repl graph through `AddRepl`. Its configuration runs when the singleton Repl application is first resolved; it must not capture request-scoped business services in module constructors.
2. Register the SDK HTTP transport, build the host, map `/mcp` and start it. The host owns process signals and service disposal.
3. For each HTTP request, the SDK creates request-local options and invokes `ConfigureSessionOptions` despite the callback's name. Pass `context.RequestServices` into the helper so projection and execution use the same HTTP scope.
4. The SDK dispatches the request to the generated tools, resources or prompts. Command execution goes through the Repl invocation pipeline and its applicable hooks. See [advanced input processing](advanced-input.md) for hook semantics.
5. The SDK cleans up its request-local server; ASP.NET Core disposes the request services, including asynchronous disposables. Cancellation of one request does not stop the host or another request.

Register shared business state as singleton and request-local dependencies as scoped in the host. Repl borrows the supplied provider without building another business-service container or opening a nested DI scope. There is no persistent per-client Repl session on this path.

### Options and Core usage

`ConfigureMcpServerOptions` replaces server identity, capabilities and the tool/resource/prompt collections with the Repl projection. It preserves other SDK settings, including handlers, filters, protocol selection and request scoping. It does not merge existing primitive collections. Configure server identity and Repl capabilities through its Repl options callback.

Both the helper and `BuildMcpServerOptions` use snapshot projection semantics. Build the application graph at startup; this integration adds no dynamic catalog notifications or cross-request session state. The example above rebuilds the catalog and generated schemas for each request; it does not cache the projection. Stateless HTTP client capabilities have the limits described under Scenario A; this example needs neither client roots nor server-to-client interactions. The static projection does not install the `DiscoverAndCallShim` compatibility handlers. Stateful and hybrid HTTP modes are not covered by this recipe.

Core applications use the same helper directly:

```csharp
var repl = CoreReplApp.Create();
repl.Map("status", () => "ready");

// Inside ConfigureSessionOptions:
repl.ConfigureMcpServerOptions(target, services: context.RequestServices);
```

For Core commands without application DI, the `services` argument may be omitted. The Defaults overload uses `repl.Services` when that argument is omitted; that is an application-wide provider, so **always supply the HTTP request provider in the hosted recipe**. Retain a projection only as long as its supplied provider remains alive.

### Validate with Inspector

The HTTP tests start their own Kestrel endpoint on an ephemeral loopback port. The opt-in Inspector test uses the same host fixture to check filtered tool discovery, schema portability and invocation, independently of the C# MCP client. Server-side rejection of excluded commands and aliases is covered by `When_ACommandIsFilteredOut_Then_DirectAndAliasCallsCannotExecuteIt` in the C# HTTP tests. It uses Inspector 2.6.0 and requires Node 22.19.0 or later:

```powershell
$env:REPL_RUN_MCP_INSPECTOR_TESTS = '1'
dotnet test --project src/Repl.McpTests/Repl.McpTests.csproj --filter FullyQualifiedName~When_InspectorCallsAStatelessHttpProjection
```

This check runs in a separate CI job; the regular .NET tests do not require Node or npm. For an application using the recipe above, Inspector can also connect directly:

```powershell
npx --yes @modelcontextprotocol/inspector@2.6.0 --cli http://localhost:5000/mcp --transport http --method tools/list --strict
```

Adjust the URL to the application's listener. To inspect it interactively, launch `npx --yes @modelcontextprotocol/inspector@2.6.0`, add a `streamable-http` connection for that URL and use the Tools tab.

## What is isolated, and at which boundary

The `2026-07-28` revision removed protocol-level sessions: the client declares its capabilities on
every request rather than once per connection. So the boundaries are not all the same size.

| Isolated per | What |
|---|---|
| Request | Client capabilities, the requested log level, the destination for sampling, elicitation and progress, and — on a reused `BuildMcpServerOptions()` result — native roots, resolved once per request and never cached across connections |
| Invocation | I/O capture — each tool call gets its own capture scope, not one per connection |
| Connection (`mcp serve` only) | The MCP session object, the native roots cache, soft roots, session-aware routing state, `IReplSessionState`, and **the DI scope**: a `Scoped` registration resolves once per connection, and its disposables are released when the connection ends |

The DI scope is the connection's, for the same reason it is the session's on every other transport: a
`Scoped` service is where per-user state lives, and state that resets between two calls of one client
is not per-user state. It is also what keeps discovery and execution honest — both resolve a module
presence predicate's services from the same scope, so a command that was advertised can be called.

The MCP SDK opens a scope of its own per request (`McpServerOptions.ScopeRequests`, default `true`) and
exposes it as `RequestContext.Services`. Repl does not run commands in it: that scope descends from the
application root and is a sibling of the connection's, so adopting it would give the catalog and the
command two different instances of the same service. `BuildDynamicServerOptions` — the `mcp serve` path
that owns a real connection context — sets `ScopeRequests = false`, so the SDK does not open that
sibling scope there to begin with.

A server created from a reused `BuildMcpServerOptions()` result — which is how an ASP.NET Core or other
custom-transport host reaches Repl — has everything above **except the connection row**: see the known
limitation above. There is no per-connection context to give a DI scope to either, so `Scoped` resolves
once from the application root and is shared by every client the process serves, for as long as it runs.
That is also true if a stateless HTTP host reuses one projection across requests: Repl's own scope is a
property of the *context* it built at construction, not of the SDK's per-request server. Scenario B
avoids this by constructing a projection for each request with that request's provider; scoped
dependencies then follow the HTTP request boundary and are disposed by ASP.NET Core.

**Calling `BuildMcpServerOptions()` again for each connection does not isolate `Scoped` either.** The
`ReplApp` overload always passes that app's one `Services` root (`app.Services`, cached for the app's
whole lifetime), so two calls against the same `ReplApp` still share the same root — and therefore the
same `Scoped` instances — no matter how often the method runs. Genuine per-connection isolation on this
path requires the host to supply a **different** `IServiceProvider` per connection, through the
`ICoreReplApp.BuildMcpServerOptions(configure, services)` overload: build a scope yourself (for example
`app.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope()`), pass its
`ServiceProvider` in for that connection, and dispose the scope yourself when the connection ends — Repl
does not manage that scope's lifetime on this path, the same way it does not manage the connection
itself. This is the "multiplexes connections over one options instance" case flagged as a caller
responsibility below, made concrete.

For those higher-level patterns, see [mcp-advanced.md](mcp-advanced.md).
