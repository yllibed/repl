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

Declare answer slots for interactive prompts so agents and `--answer:` flags can provide values:
```csharp
app.Map("delete {id:int}", handler)
    .Destructive()
    .WithAnswer("confirm", "bool", "Confirm the deletion");
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
    [Description("Clear the screen")]
    static async (IReplInteractionChannel ch, CancellationToken ct) =>
        await ch.ClearScreenAsync(ct)));
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

See also: [Modules](module-presence.md) | [Route System](route-system.md) | [MCP Server](mcp-server.md) | [Testing](testing-toolkit.md) | [Configuration](configuration-reference.md)
