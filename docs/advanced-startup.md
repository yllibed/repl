# Advanced Startup Preparation and Hooks

These opt-in APIs serve legacy command lines and applications where Repl is only
one component. Ordinary applications should start with `Create`, `Map` and
`RunAsync`. Startup hooks complement
[argument adaptation and input validation](advanced-input.md); they do not run on
every edited line or command within an existing session.

## Preparation Before Composition

Declare options and adapters on a separate builder, then prepare before creating
application objects or registering application services:

```csharp
using Repl;

var builder = ReplStartup.CreateBuilder()
    .UseGlobalOptions<StartupOptions>()
    .UsePreparationHook((StartupOptions options) =>
        ArgumentPreprocessingResult.Continue(options.Port > 0
            ? InputValidationResult.Valid()
            : InputValidationResult.Invalid("Port must be positive.")));

var prepared = await builder.PrepareAsync(args);
if (!prepared.CanContinue)
{
    return await prepared.CompleteAsync();
}

var options = prepared.GetOptions<StartupOptions>();
// Construct application objects here, using options.Port.
var app = CoreReplApp.Create(prepared);
app.UseStartupHook((ReplStartupContext context, StartupOptions current,
    CancellationToken cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    return context.RemainingArguments.Count == 0
        ? ArgumentPreprocessingResult.Replace(["serve"])
        : ArgumentPreprocessingResult.Continue();
});
app.Map("serve", (StartupOptions current) => $"Serving on {current.Port}.");
return await app.RunAsync(prepared);

public sealed class StartupOptions
{
    public int Port { get; set; } = 8080;
}
```

Core uses no DI package. Its hooks receive context, typed global options and
`CancellationToken`; application dependencies can be captured in closures.
Preparation hooks cannot request application services or `IServiceProvider`.

`OriginalArguments` retains the initial array; `Arguments` is the effective array.
`RemainingArguments` excludes parsed globals. `GlobalOptions` exposes individual
values, and `GetOptions<T>()` creates a fresh object of a declared options type.
No command needs to exist during preparation. Invalid globals produce diagnostics
before application service registration. `CompleteAsync` renders a terminal result
or refusal under the configured output and exit policies, without application DI.

The builder freezes its declarations on the first preparation. It may prepare
independent argument arrays afterward, including concurrently. Configuration
callbacks supplied to `Options` are applied to independent preparation/application
instances; keep those callbacks reusable and free of external actions.

## DI Construction and Startup

Use the same declarations when creating the DI-enabled facade:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Repl;

var prepared = await ReplStartup.CreateBuilder()
    .UseGlobalOptions<StartupOptions>()
    .PrepareAsync(args);
if (!prepared.CanContinue)
{
    return await prepared.CompleteAsync();
}

var app = ReplApp.Create(prepared, services =>
    services.AddSingleton(new Listener(prepared.GetOptions<StartupOptions>().Port)));

app.UseStartupHook((ReplStartupContext context, Listener listener) =>
{
    if (context.Purpose is InputPurpose.Help or InputPurpose.Completion)
    {
        return ArgumentPreprocessingResult.Continue();
    }
    return context.RemainingArguments is ["/port"]
        ? ArgumentPreprocessingResult.Handled(listener.Port)
        : ArgumentPreprocessingResult.Continue();
});
app.Map("serve", (Listener listener) => $"Serving on {listener.Port}.");
return await app.RunAsync(prepared);

public sealed class StartupOptions { public int Port { get; set; } = 8080; }
public sealed record Listener(int Port);
```

`MapModule<T>()` can construct the shared provider during mapping. On this explicit
path, preparation has already finished before the application or any such module
exists. Do not call `UseGlobalOptions<T>()` again on the created app: the prepared
declarations transfer to it, including typed option injection.

Startup hooks also work without explicit preparation:

```csharp
var app = ReplApp.Create();
app.UseStartupHook((ReplStartupContext context) =>
    ArgumentPreprocessingResult.Continue());
```

This does not provide a before-DI guarantee. Its provider and session scope already
exist when argument adaptation and startup hooks run.

## Callback and Lifecycle Order

The explicit preparation path has this order:

| Step | Callback or work | Services available |
| --- | --- | --- |
| 1 | Prepared argument adapters, in registration order | No application services |
| 2 | Global parsing and conversion | No application services |
| 3 | Preparation hooks, in registration order | Context, typed globals, cancellation |
| 4 | Application construction, registration and module mapping | Application-owned composition |
| 5 | `RunAsync`: I/O, signal policy and session scope setup | Run/session services |
| 6 | Any adapters added to the app after preparation | Run/session services |
| 7 | Startup hooks, in registration order | Context, current typed globals and scoped services |
| 8 | Start `IHostedService` instances, only in `Head` mode | Root provider |
| 9 | Normal help/completion/command dispatch and input validation | Run/session services |
| 10 | Handler binding, context validation, middleware and handler | Run/session services |
| 11 | Render the result and dispose Repl-owned session scope | Existing run cleanup policy |
| 12 | Stop started hosted services, then resolve the final exit code | Existing lifecycle ownership |

For ordinary `RunAsync(args)`, provider/scope setup comes first, followed by its
argument adapters and startup hooks. Without these features, existing behavior is
unchanged. A caller-owned scope is not disposed by Repl; an external host retains
ownership of its provider, signals and hosted services. A host that has already
started its services cannot be moved back to the before-start stage.

Hooks support synchronous, `Task` and `ValueTask` decisions:

- `Continue` advances to the next hook.
- `Replace` changes effective arguments and reparses their globals before the next
  hook. Normal command parsing and validation still apply to the final input.
- `Handled` prevents later hooks, hosted-service startup and dispatch. It completes
  this startup/run; it never opens an interactive session just to handle a result.

Already executed preparation callbacks are not replayed by `RunAsync(prepared)`.
Whole argument arrays are retained; no shell command string is constructed. A
rewrite never rebuilds DI. The prepared snapshot still describes the values used
to compose services, while later hooks see the effective values after rewriting.

Startup hooks run once for each run/session. They do not run for subsequent
interactive commands, background editor analysis or nested MCP tool calls.
Ordinary argument adapters and input validators retain their per-input behavior.
Hooks run for help and completion too: check `Purpose` before external actions.
An empty invocation can have `Unknown` purpose. Shell completion envelopes are
identified before startup actions; their payload retains normal shell adaptation.

Failures stop the remaining hooks. Hook exceptions use an application-failure
outcome and the effective requested output format. Actual caller cancellation
follows the configured cancellation policy; an unrelated cancellation exception
is a callback failure. Preparation cancellation propagates to its caller.

## Externally Owned Providers

Prepare before constructing the host, then register the application into its
service collection. For example, using the options and listener above:

```csharp
var prepared = await ReplStartup.CreateBuilder()
    .UseGlobalOptions<StartupOptions>().PrepareAsync(args);
if (!prepared.CanContinue)
{
    return await prepared.CompleteAsync();
}

var services = new ServiceCollection();
services.AddRepl(prepared,
    registrations => registrations.AddSingleton(
        new Listener(prepared.GetOptions<StartupOptions>().Port)),
    (provider, app) =>
    {
        app.UseStartupHook((Listener listener) =>
            ArgumentPreprocessingResult.Continue());
        app.Map("serve", (Listener listener) => $"Serving on {listener.Port}.");
    });

await using var provider = services.BuildServiceProvider();
var app = provider.GetRequiredService<ReplApp>();
return await app.RunAsync(prepared, provider);
```

The registration callback runs before provider construction. The mapping callback
receives the completed host provider; resolve module instances from that provider
there rather than calling `MapModule<T>()` against the app's internal container.
No intermediate host provider is built by the preparation API.

Prepared input can be reused with applications created from the same builder.
`Create` and all prepared `RunAsync` overloads throw `InvalidOperationException`
for terminal preparation; use `CompleteAsync` to render that outcome instead.
Each run has independent hook progress and scoped state; do not mutate application
configuration while runs are active. Hooks remain application code and must
protect their own external resources when concurrent runs use them.

See the existing [advanced-input sample](../samples/09-advanced-input/) and its
`demo` for a policy selected before DI and a guarded startup refresh that either
continues or finishes without a command. Per-invocation programmatic/MCP
interception remains [issue #115](https://github.com/yllibed/repl/issues/115).
