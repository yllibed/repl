# Repl (meta-package)

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

- Architecture blueprint: `https://github.com/yllibed/repl/blob/main/docs/architecture.md`
- Terminal/session metadata: `https://github.com/yllibed/repl/blob/main/docs/terminal-metadata.md`
- Testing toolkit: `https://github.com/yllibed/repl/blob/main/docs/testing-toolkit.md`
- Samples (recommended learning path): `https://github.com/yllibed/repl/blob/main/samples/README.md`
