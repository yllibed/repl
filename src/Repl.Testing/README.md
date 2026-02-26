# Repl.Testing

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

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

- Testing toolkit guide: [docs/testing-toolkit.md](https://github.com/yllibed/repl/blob/main/docs/testing-toolkit.md)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
