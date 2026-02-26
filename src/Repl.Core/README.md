# Repl.Core

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

`Repl.Core` is the **dependency-free** runtime core of Repl Toolkit (no `Microsoft.Extensions.*` runtime dependencies).

It contains the fundamentals: route templates + constraints, option/argument binding, typed results, help/discovery, and middleware.

## Install

```bash
dotnet add package Repl.Core
```

## Minimal app

```csharp
using Repl;

var app = CoreReplApp.Create();
app.Map("hello", () => "world");

return app.Run(args);
```

## When to use something else

- Want DI + default interactive UX: use `Repl.Defaults` (or `Repl` meta-package).
- Want in-memory multi-session tests: use `Repl.Testing`.

## Docs

- Project overview: [README.md](https://github.com/yllibed/repl/blob/main/README.md)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
