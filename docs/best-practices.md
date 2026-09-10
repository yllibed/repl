# Best Practices

## Use rich parameter types

Prefer typed route constraints over raw strings. Typed parameters give you:

- Automatic validation at route matching time
- Better help text and MCP schema generation
- Correct .NET types in handler parameters

```csharp
// Prefer this
app.Map("user {id:int}", (int id) => ...);
app.Map("open {path:uri}", (Uri path) => ...);
app.Map("since {date:date}", (DateOnly date) => ...);
app.Map("export {file}", (FileInfo file) => ...);

// Over this
app.Map("user {id}", (string id) => int.Parse(id));
```

Available types: `int`, `long`, `bool`, `email`, `uri`, `url`, `date`, `datetime`, `timespan`, `guid`, and implicit `FileInfo`/`DirectoryInfo`. See [Route System](route-system.md).

For temporal queries, use `ReplDateRange` for human-friendly range syntax:

```csharp
app.Map("logs {range}", (ReplDateRange range) => ...);
// Accepts: "today", "last-7d", "2024-01-01..2024-03-01"
```

## Prefer static lambdas and DI injection

Use `static` lambdas to avoid captures. Inject services through handler parameters instead of closures. This enables expression tree compilation and keeps handlers testable.

```csharp
// Prefer this — static lambda, services injected
app.Map("list", static (IContactStore store) => store.All());

app.Map("add {name} {email:email}",
    static (string name, string email, IContactStore store) =>
    {
        store.Add(new Contact(name, email));
        return Results.Success($"Added {name}.");
    });

// Avoid this — closure captures
var store = new ContactStore();
app.Map("list", () => store.All());  // captures 'store'
```

## Register services with DI

Use `ReplApp.Create(services => ...)` for service registration. Prefer constructor injection in modules and implicit injection in handlers.

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IContactStore, InMemoryContactStore>();
    services.AddSingleton(typeof(IEntityStore<>), typeof(InMemoryEntityStore<>));  // open-generic
});
```

Use `[FromServices]` only when disambiguation is needed (e.g., same type available from both context and DI). Otherwise, implicit injection works:

```csharp
app.Map("show", static (IContactStore store) => store.All());  // implicit
app.Map("show", static ([FromServices] IContactStore store) => store.All());  // explicit (same result)
```

Use `[FromContext]` to access route values from parent contexts:

```csharp
app.Context("project {id:int}", project =>
{
    project.Map("status", static ([FromContext] int id, IProjectService svc) =>
        svc.GetStatus(id));
});
```

## Use global options for cross-cutting configuration

When configuration applies to all commands (tenant, environment, verbosity), register global options and access them via `IGlobalOptionsAccessor`:

```csharp
app.Options(o =>
{
    o.Parsing.AddGlobalOption<string>("tenant");
    o.Parsing.AddGlobalOption<bool>("verbose");
});
```

For many global options, prefer `UseGlobalOptions<T>()` with a typed class:

```csharp
app.UseGlobalOptions<MyGlobalOptions>();
```

Use `IGlobalOptionsAccessor` in DI factories for services that depend on global option values:

```csharp
services.AddSingleton<ITenantClient>(sp =>
{
    var globals = sp.GetRequiredService<IGlobalOptionsAccessor>();
    return new TenantClient(globals.GetValue<string>("tenant", "default")!);
});
```

Note: DI singleton factories are resolved lazily, so the values are available after global option parsing completes. However, singleton factories capture values once — in interactive mode, global options can change between commands. If your service needs to see updated values per command, inject `IGlobalOptionsAccessor` directly and read values at call time instead of capturing them in a factory. See [Commands — Accessing global options](commands.md#accessing-global-options-outside-handlers).

## Group related options with `[ReplOptionsGroup]`

When a command has many options, group them into a class instead of listing them all as handler parameters. This keeps handlers clean and makes option sets reusable across commands.

```csharp
[ReplOptionsGroup]
public class PagingOptions
{
    [ReplOption(Aliases = ["-n"])]
    public int Limit { get; set; } = 20;

    [ReplOption]
    public int Offset { get; set; }
}

[ReplOptionsGroup]
public class FilterOptions
{
    [ReplOption(Aliases = ["-q"])]
    public string? Query { get; set; }

    [ReplOption]
    public bool IncludeArchived { get; set; }
}
```

Inject them directly into handlers — the framework binds each property from parsed options:

```csharp
app.Map("list", static (IContactStore store, PagingOptions paging, FilterOptions filter) =>
    store.Query(filter.Query, filter.IncludeArchived, paging.Offset, paging.Limit));
```

Options groups compose well — you can combine multiple groups in the same handler, and reuse them across different commands.

## Structure commands with modules

Use `IReplModule` for reusable command groups. Modules keep command definitions cohesive and composable.

```csharp
public sealed class ContactModule : IReplModule
{
    public void Map(IReplMap map)
    {
        map.Map("list", static (IContactStore store) => store.All())
            .WithDescription("List all contacts")
            .ReadOnly();

        map.Map("add {name} {email:email}", static (string name, string email, IContactStore store) =>
            { store.Add(new(name, email)); return Results.Success($"Added {name}."); })
            .WithDescription("Add a contact");
    }
}
```

Mount modules in contexts, reuse across scopes:

```csharp
app.Context("contacts", contacts => contacts.MapModule<ContactModule>());
```

## Use conditional module presence

Control command visibility per runtime channel:

```csharp
app.MapModule(
    new AdminModule(),
    static context => context.Channel is ReplRuntimeChannel.Cli);  // CLI-only

app.MapModule(
    new DiagnosticsModule(),
    static (FeatureFlags flags) => flags.DiagnosticsEnabled);  // feature-gated
```

Call `app.InvalidateRouting()` if presence conditions can change at runtime.

## Design dynamic contexts with validation

Always validate dynamic context segments to prevent invalid scopes:

```csharp
app.Context("{name}", scope =>
{
    scope.Map("show", static (string name, IContactStore store) => store.Get(name));
    scope.Map("remove", static (string name, IContactStore store) =>
    {
        store.Remove(name);
        return Results.NavigateUp($"Removed '{name}'.");
    });
},
validation: static (string name, IContactStore store) => store.Get(name) is not null);
```

Validation delegates support DI injection for service-backed checks.

## Annotate commands for MCP and automation

Behavioral annotations improve AI agent discoverability and safety:

```csharp
app.Map("status", static () => GetStatus())
    .WithDescription("Get system status")
    .ReadOnly()
    .AsResource();  // exposed as MCP resource

app.Map("deploy {env}", static (string env) => Deploy(env))
    .WithDescription("Deploy to environment")
    .WithDetails("Triggers a full deployment pipeline to the target environment.")
    .Destructive()
    .OpenWorld();

app.Map("troubleshoot {symptom}", static (string symptom) =>
    $"Investigate: '{symptom}'. Use status and logs tools first.")
    .WithDescription("Diagnostic guidance")
    .AsPrompt();  // exposed as MCP prompt

app.Map("clear", static async (IReplInteractionChannel ch, CancellationToken ct) =>
    { await ch.ClearScreenAsync(ct); })
    .AutomationHidden();  // not exposed to agents
```

For MCP Apps, mark the HTML-producing command as an app resource:

```csharp
app.Map("contacts dashboard", static (IContactStore contacts) => BuildHtml(contacts))
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource();
```

This lets capable hosts render the UI while keeping raw HTML out of the model-facing transcript. The handler is still a normal Repl mapping, so it can use DI, cancellation tokens, and the usual command pipeline.

Declare answer slots for interactive prompts so agents and `--answer:` flags can provide values:

```csharp
app.Map("delete {id:int}", handler)
    .Destructive()
    .WithAnswer("confirm", "bool", "Confirm the deletion");
```

## Make exit codes scriptable

A headless tool — one command per process, spawned by CI or by a parent program — is judged by its
exit code. Repl classifies every run into a `ReplExecutionOutcomeKind` and maps it through
`ReplOptions.ExitCodes`, so the contract is configured once instead of being re-implemented in every
handler:

```csharp
app.Options(options =>
{
    options.ExitCodes.Help = 3;         // a bare invocation printed help and did no work
    options.ExitCodes.UsageError = 64;  // EX_USAGE: the caller typed it wrong
    options.ExitCodes.Cancelled = 130;  // return 128+SIGINT instead of throwing
});
```

- Keep usage errors (`2` by default) distinct from handler failures (`1`) so a pipeline can tell a
  refused invocation from a tool that broke. Note the other side of that default: `HandlerError`,
  `HandlerException` and `FrameworkError` all map to `1`, so "the command failed" and "the framework
  failed" are indistinguishable out of the box — give `ExitCodes.FrameworkError` its own code if a
  broken tool must alert differently from a failing command. Note that `BindingError` shares that `2`, and it also
  covers values the binder resolves itself — a missing DI registration or an unresolvable
  `[FromServices]` dependency is an application-wiring defect, not a caller mistake. Give it its own
  code if your contract needs to separate the two.
- Keep codes within `0`-`255`: POSIX `wait` exposes only the low eight bits, so `300` reaches a shell
  as `44`.
- Map `Help` to a non-zero code when a bare invocation must not pass a CI step that forgot its
  arguments.
- Use `Results.Exit(code)` for codes a specific command owns; use `ExitCodes.Resolver` to apply an
  organisation-wide convention to every final outcome — it also sees the `Result` object and the
  `Exception`, so it can map on an error code rather than on a message.
- A handler's `int` return value is **data**, rendered like any other value; it never becomes the
  exit code.
- Set `ExitCodes.Cancelled` when the caller owns a `CancellationToken` and wants an integer rather
  than an `OperationCanceledException` escaping `RunAsync`. Installing a `Resolver` opts in to the
  same thing: cancellation then reaches the hook instead of propagating, so a resolver written to map
  "every final outcome" really sees every one. With only a resolver installed, the code it is handed
  for a cancellation is `130` — never `1` — so `outcome.ExitCode` stays distinguishable from a handler
  failure even without a table entry.
- `ExitCodes.Resolver` is the only public seam that observes the outcome *kind*: middleware runs
  before classification, so a resolver is where an audit trail of exit codes belongs. It does not fire
  for a run that ends by propagating an exception, nor for nested MCP sub-invocations.

```csharp
options.ExitCodes.Resolver = outcome =>
{
    logger.LogInformation(
        "run ended {Kind} → {ExitCode} ({Scope})", outcome.Kind, outcome.ExitCode, outcome.Scope);
    return outcome.ExitCode;   // observe without changing the contract
};
```

## Write deterministic tests

Use `ReplTestHost` for integration tests with typed results:

```csharp
await using var host = ReplTestHost.Create(() => BuildApp());

// One-shot command
var result = await host.RunCommandAsync("contacts list --json");
result.ExitCode.Should().Be(0);
var contacts = result.GetResult<Contact[]>();

// Multi-session
await using var session = await host.OpenSessionAsync();
await session.RunCommandAsync("contacts add Alice alice@test.com");
var list = await session.RunCommandAsync("contacts list");
list.OutputText.Should().Contain("Alice");
```

## Polish the interactive experience

Register ambient commands for common actions:

```csharp
app.Options(o => o.AmbientCommands.MapAmbient(
    "clear",
    static async (IReplInteractionChannel ch, CancellationToken ct) =>
        await ch.ClearScreenAsync(ct),
    "Clear the screen"));
```

Seed history for discoverability:

```csharp
services.AddSingleton<IHistoryProvider>(new InMemoryHistoryProvider([
    "contacts list", "contacts add", "status"
]));
```

Use Spectre.Console for rich UI — prompts auto-upgrade transparently:

```csharp
app.UseSpectreConsole();  // existing IReplInteractionChannel calls render as Spectre prompts
```

When Spectre owns the screen, treat that as a separate rendering surface. Use `IAnsiConsole` for one-shot renderables and prompts, but do not mix a full-screen/live Spectre UI with regular REPL feedback on the same surface. If your app enters a TUI or live display flow, resolve `SpectreInteractionPresenter` from DI and capture interaction output for the duration of that scope:

```csharp
app.Map("dashboard", static async (
    SpectreInteractionPresenter presenter,
    IReplIoContext io,
    CancellationToken ct) =>
{
    using var capture = presenter.BeginCapture(io.Error);
    await RunDashboardAsync(ct);
});
```

That keeps status/progress/problem events out of the main Spectre surface and avoids terminal control sequences fighting with your TUI.

## Own process signals exactly once

Use `UseCliProfile()` (or an explicit `ProcessSignalHandlingMode.Automatic`) for a standalone CLI where Repl is the process owner. An unprofiled `ReplApp.Create()` remains caller-owned. Use `UseEmbeddedConsoleProfile()` or explicitly set `ProcessSignalHandlingMode.None` when an ASP.NET Core host, worker service, test runner, or another command framework already owns console cancellation and shutdown. Feed that host's cancellation token into `RunAsync` instead of installing competing handlers. External `IServiceProvider`, `IHost`, and `IReplHost` overloads always remain caller-owned and diagnose an explicit `Automatic` request instead of applying it.

Supplying a `ReplRunOptions` instance for an unrelated setting preserves the profile default because `ProcessSignalHandling` is nullable:

```csharp
var app = ReplApp.Create().UseEmbeddedConsoleProfile();

return await app.RunAsync(
    args,
    new ReplRunOptions
    {
        AnsiSupport = AnsiMode.Never,
    },
    hostStoppingToken);
```

A one-shot handler token injected during `Automatic` handling is run-scoped; an interactive command receives a shorter-lived token linked to that run token. Await all work that uses either token before returning, and do not capture it for detached background work. See [Process signal handling](configuration-reference.md#process-signal-handling) for first/second-signal behavior, exit-code conventions, and platform limits.

See also: [Modules](module-presence.md) | [Route System](route-system.md) | [MCP Overview](mcp-overview.md) | [Testing](testing-toolkit.md) | [Configuration](configuration-reference.md)
