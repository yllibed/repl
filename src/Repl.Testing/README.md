# Repl.Testing

`Repl.Testing` is an in-memory harness for **multi-step** and **multi-session** tests over a Repl command surface.

## Install

```bash
dotnet add package Repl.Testing
```

## Example

```csharp
using Repl;
using Repl.Testing;

await using var host = ReplTestHost.Create(() =>
{
    var app = ReplApp.Create().UseDefaultInteractive();
    app.Map("hello", () => "world");
    return app;
});

await using var session = await host.OpenSessionAsync();
var execution = await session.RunCommandAsync("hello --no-logo");
```

## Docs

- Testing toolkit guide: `https://github.com/yllibed/repl/blob/main/docs/testing-toolkit.md`
