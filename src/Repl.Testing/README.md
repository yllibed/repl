# Repl.Testing

**Website:** [repl.yllibed.org](https://repl.yllibed.org/)

`Repl.Testing` is an in-memory harness for **multi-step** and **multi-session** tests over a Repl command surface.

It also covers the process-signal lifecycle: `ReplProcessSignalHarness` drives SIGINT, SIGTERM and
Ctrl+Break in memory — deterministically, and for any platform's decisions from any host — and
`ReplProcessProbe` spawns your application to show what only a real process can, that it terminates
with the code a shell sees.

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

- [Cookbook: Testing](https://repl.yllibed.org/cookbook/testing/) — test host setup, typed assertions, multi-session, interaction supply
- [Best Practices](https://repl.yllibed.org/reference/best-practices/) — test-first patterns and testing at the command level
- [Testing toolkit: process signals](https://repl.yllibed.org/cookbook/testing/#process-signals) — signal tests, declaring a platform, and what needs a spawned process
