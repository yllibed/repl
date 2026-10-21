# Remote CLI and local administration

`Repl.Remote` provides a client and server for invoking commands in an existing process. `Repl.Remote.Defaults` connects that protocol to a `ReplApp`. These are advanced hosting features: use them when Repl is one component of a larger application, or when an existing executable needs a compatible administration entry point.

For a runnable example, use [sample 05](../samples/05-hosting-remote/). The same process serves browser terminals, native console sessions and one-shot commands against shared application services.

## Packages and boundaries

| Package | Responsibility |
| --- | --- |
| `Repl.Remote` | Portable JSON contracts, stream server/client, same-user named-pipe listener and native console client |
| `Repl.Remote.Defaults` | `ReplAppRemoteBackend`, per-invocation DI scopes and `StreamedReplHost` terminal sessions |

The protocol package references neither Core nor Defaults. `IRemoteReplBackend` and `IRemoteTerminalSession` are the adapter boundary; an adapter for Core can implement them without changing clients, framing or JSON messages. The Core-only adapter is tracked in [#132](https://github.com/yllibed/repl/issues/132). No service provider, CLR type name or serialized exception is part of the wire contract.

MCP and this administration protocol serve different consumers. An MCP projection can expose selected tools to agents. The local CLI endpoint exposes the application's administrative command surface to trusted operators.

## Hosting an existing application

```csharp
using Repl;
using Repl.Remote;

// Select --remote-exec / --remote-console before constructing this host.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRepl((services, app) =>
{
    app.UseEmbeddedConsoleProfile();
    app.Map("health", () => new { status = "ready" });
});

await using var host = builder.Build();
var app = host.Services.GetRequiredService<ReplApp>();
await using var remote = new NamedPipeReplServer("my-app",
    _ => ValueTask.FromResult<IRemoteReplBackend>(
        new ReplAppRemoteBackend(app, host.Services)));

await host.StartAsync();
await host.WaitForShutdownAsync();
```

Use the host's real readiness state for a production health command. A successful connection alone proves only that the listener is accepting connections. A health command should check the dependencies that determine readiness and return an appropriate exit code.

The order of startup and callbacks is:

1. The executable selects client mode before building host services. Client mode connects to the existing process and returns its remote exit code.
2. Server mode builds the host and configures the Repl graph. The application retains ownership of its root provider and hosted services.
3. The listener starts accepting connections. A backend is created for each connection. In the example above it borrows the existing `ReplApp` and host provider.
4. `cli.describe` creates and disposes a scope to discover the graph with the application's services. `cli.execute` creates a separate scope and I/O session, then uses Repl's programmatic invocation pipeline: input preprocessing, invocation hooks, validation, binding, middleware, handler and result rendering. It does not start another application host or install process-signal handlers. Application and module exit-code policies apply to the result.
5. `terminal.open` creates a session scope and starts a hosted interactive run through `StreamedReplHost`. Normal startup callbacks run for that session; subsequent commands follow the interactive pipeline. See [startup callbacks](advanced-startup.md) and [input hooks](advanced-input.md) for their detailed order.
6. Closing or losing a connection cancels its work. Terminal interruption cancels only that session's active command; an idle prompt remains open. Detach does not stop the serving application.
7. Listener disposal stops acceptance and cancels connections before the host provider is disposed. Backend implementations must cooperate with cancellation. Shutdown waits are bounded; arbitrary application code cannot be forcibly stopped safely.

When no provider is supplied to `ReplAppRemoteBackend`, it borrows `app.Services`. Supplying the embedding host's provider keeps its singleton services shared. The adapter never disposes the application or that root provider.

## Clients and stream ownership

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await using var client = await NamedPipeReplServer.ConnectAsync("my-app", deadline.Token);
await client.DiscoverAsync(deadline.Token);
var result = await client.ExecuteAsync(new RemoteInvocationRequest
{
    Arguments = ["health"],
}, deadline.Token);
return result.ExitCode;
```

For an interactive client, call `RemoteConsole.RunAsync(client, cancellationToken)`. It requires a real console. Ctrl+C interrupts the remote command; Ctrl+D detaches. Local console state is restored on normal exit, cancellation and failure. Consume `RemoteTerminal.Output` continuously when writing a custom terminal client.

`new RemoteReplClient(stream)` and `RemoteReplServer.RunAsync(stream, backend, token)` borrow their streams. Their two-stream overloads also support redirected standard I/O. Callers close those streams and dispose the backend. The named-pipe listener instead owns its accepted streams and backend instances; the named-pipe client factory owns the pipe it creates.

The stream server drains in-flight backend calls before returning. Listener shutdown waits up to five seconds, then throws `TimeoutException` if application code is still running; it retains those backends until their calls finish. The embedding application must keep borrowed services alive until that work has drained.

A connection permits one active execution or terminal. Different connections can execute concurrently; handlers must protect shared mutable state as they would for other hosted transports. A busy connection returns error `-32001`. A terminal identifier belongs only to its connection; a missing terminal returns `-32002`.

## Wire profile

The envelope is [JSON-RPC 2.0](https://www.jsonrpc.org/specification). Discovery uses [OpenRPC](https://spec.open-rpc.org/), with JSON Schema Draft 7 value schemas. The authoritative method and DTO description is [openrpc.json](../src/Repl.Remote/openrpc.json), also returned by `rpc.discover`.

The CLI method names, `x-cli` discovery extension and terminal conventions are an **experimental Repl profile**, not an established industry CLI standard. The profile version is separate from the OpenRPC document version. Clients check the profile's major version and inspect capabilities; compatible extensions can add methods and optional JSON fields. A breaking wire change requires another profile major version.

Messages use UTF-8 JSON preceded by ASCII `Content-Length: <byte-count>\r\n\r\n`. Count encoded bytes, not characters. This is stream framing, not HTTP. [StreamJsonRpc](https://microsoft.github.io/vs-streamjsonrpc/docs/connecting.html) handles framing, request correlation and cancellation in the .NET implementation. Python interoperability tests exercise the same protocol without that library.

| Method | Direction | Purpose |
| --- | --- | --- |
| `rpc.discover` | Client → server | OpenRPC document, profile version and capabilities |
| `cli.describe` | Client → server | Contexts, canonical routes, aliases and parameter schemas |
| `cli.execute` | Client → server | Execute once and return outcome, exit code, JSON result, stdout and stderr |
| `terminal.open` | Client → server | Start a terminal with a client-generated `sessionId` and initial metadata |
| `terminal.input` | Client → server | Text and VT sequences, in chunks of at most 16,384 UTF-16 code units |
| `terminal.resize` | Client → server | Positive column and row counts |
| `terminal.interrupt` | Client → server | Cancel the active command while keeping the session |
| `terminal.close` | Client → server | Cancel and close that session |
| `terminal.output` | Server notification | Text and VT sequences for a `sessionId` |
| `terminal.exited` | Server notification | Final exit code for a `sessionId`, after output drains |
| `$/cancelRequest` | Notification | Cancel a pending request by JSON-RPC ID |

`$/cancelRequest` is a shared JSON-RPC ecosystem convention, not part of the JSON-RPC 2.0 specification itself. Cancellation is cooperative. A transport failure never triggers an automatic retry: the command might already have run, even when the response was lost.

An execution supplies exactly one representation:

```json
{ "arguments": ["items", "show", "42"] }
```

```json
{
  "command": "items show {id}",
  "values": { "id": 42 },
  "globalValues": { "tenant": "demo" }
}
```

Structured commands use registered route templates and canonical parameter keys from discovery. JSON arrays and objects remain native values. The Defaults adapter forces JSON output and prevents automatic interactive entry for `cli.execute`. Returned JSON is separate from handler writes through `IReplIoContext.Output` and `.Error`; multiple rendered results are returned as a JSON array. Write through these injected streams rather than process-global `Console` APIs.

Discovery follows Repl's metadata. Explicit service parameters are omitted; implicit service fallback parameters may remain optional CLI inputs. Unknown value types receive an unconstrained schema rather than a CLR type name. Hidden commands are omitted from discovery but remain invocable: hiding is not authorization.

## Local security and limits

The supplied listener uses `PipeOptions.CurrentUserOnly` on both ends. On Windows, server and client must also have compatible elevation. On Unix, .NET implements these pipes with Unix-domain sockets; clients use the logical pipe name rather than relying on an internal socket path. See [.NET named pipes](https://learn.microsoft.com/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication). Keep one listener per endpoint name; an endpoint collision fails instead of silently starting another server.

This endpoint is intended for trusted local administration. It does not provide a network authentication layer, per-command authorization or a sandbox for handlers. A caller supplying another transport must establish those boundaries before passing its streams to the server. Terminal output contains application-controlled VT sequences.

Terminal output buffers are bounded; a slow consumer causes a visible connection failure rather than silent text loss. This first implementation captures one-shot output in memory and relies on the application's input and result sizes. It is suited to modest administrative commands, not bulk data transfer or untrusted traffic.

## Delegation and future adapters

The integration suite includes `A → B → C`: B forwards an invocation and its cancellation token to another client, and the result returns to A. Reentering an already-active connection is refused with busy. This is a connection ownership rule, not general cycle detection.

There is no automatic command-graph linking in this profile. A future delegation layer must reject static graph cycles and carry an explicit hop budget across newly opened connections. A timeout bounds waiting but does not prove that a cycle was detected. Such routing metadata can be introduced as an optional profile extension without introducing DI or Defaults dependencies into the base contract.
