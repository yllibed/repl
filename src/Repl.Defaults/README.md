# Repl.Defaults

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

`Repl.Defaults` layers DI and “batteries included” composition on top of `Repl.Core`.

It provides:

- `ReplApp` facade
- default composition profiles (e.g. `UseDefaultInteractive`)
- hosted-session primitives (`StreamedReplHost`) used by transport integrations

## Install

```bash
dotnet add package Repl.Defaults
```

## Minimal app

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", () => "world");

return app.Run(args);
```

## Docs

- Architecture blueprint: [docs/architecture.md](https://github.com/yllibed/repl/blob/main/docs/architecture.md)
- Terminal metadata model: [docs/terminal-metadata.md](https://github.com/yllibed/repl/blob/main/docs/terminal-metadata.md)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
