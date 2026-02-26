# Repl (meta-package)

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

**Repl Toolkit** is a **foundational building block** for .NET applications that need a serious command surface.

---

`Repl` is the recommended starting point for **Repl Toolkit**. It brings the default dependencies:

- `Repl.Core`
- `Repl.Defaults`
- `Repl.Protocol`

## Minimal app

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", () => "world");

return app.Run(args);
```

## Docs

- Architecture blueprint: [docs/architecture.md](https://github.com/yllibed/repl/blob/main/docs/architecture.md)
- Terminal/session metadata: [docs/terminal-metadata.md](https://github.com/yllibed/repl/blob/main/docs/terminal-metadata.md)
- Testing toolkit: [docs/testing-toolkit.md](https://github.com/yllibed/repl/blob/main/docs/testing-toolkit.md)
- Samples (recommended learning path): [samples/README.md](https://github.com/yllibed/repl/blob/main/samples/README.md)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
