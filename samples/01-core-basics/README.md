# 01 — Core Basics
**From “parse args” to a real command surface (Repl.Core only)**

This first demo is intentionally small and dependency-light. It uses **Repl.Core only** (no DI, no hosting extras) to show the **essentials**:

- one **command graph** shared by CLI and REPL,
- **help** and **discovery** with zero extra authoring,
- **typed parameters** and **route constraints**,
- sensible **default rendering** (tables vs text),
- and **agent-friendly discovery** via `--help`.

Think of this as the *Hello, World* of Repl Toolkit: a tiny **contacts** app that already behaves like a serious tool.

---

## ⚡ 12-seconds tour

**One-shot CLI**

```text
$ myapp add "Carla Roy" carla@example.com --json
{ "id": 1, "name": "Carla Roy", "email": "carla@example.com" }

$ myapp show 1 --json
{ "id": 1, "name": "Carla Roy", "email": "carla@example.com" }
```

**Discovery comes for free**

```text
$ myapp --help
Commands:
  list
  add {name} {email}
  show {id}
  count
```

**Same commands, interactive**

```text
$ myapp
Try: list, add Alice alice@test.com, show 1, count

> add "Carla Roy" carla@example.com
Contact 'Carla Roy' added.

> list
Name       Email
Carla Roy  carla@example.com

> exit
```

No separate “CLI mode”. No separate “REPL mode”.  
Same routes. Same handlers. Same behavior.

> This is the same command surface, just executed in different modes.
---

## What you are seeing

This sample validates the **core contract** of Repl Toolkit:

- **One command graph** authored from delegates
- **Dual mode**: one-shot CLI *and* interactive REPL use the same routes
- **Route constraints** like `{id:int}` and `{email:email}`
- **Metadata from attributes**:
  - `[Description]` on handlers → command descriptions in help
  - `[Description]` on parameters → argument descriptions in help
  - `[Browsable(false)]` → hide commands from discovery
- **Help and discovery** without writing any help text manually
- **Reasonable defaults**:
  - collections render as tables,
  - strings render as-is,
  - structured data can be emitted as JSON via `--json`

This is Repl Toolkit at its smallest: just **Repl.Core**, no DI, no hosting, no extras.

---

## The command model (mental picture)

```
myapp
├── list
├── add {name} {email}
├── show {id:int}
└── count
```

- There is **no** `help` node in the graph.
- There is **no** `--help` command you define.
- Help is a **framework behavior**:
  - `--help` in CLI intercepts execution and renders help.
  - `help` in REPL is an ambient command provided by the framework.
- Both render the **same model**.

---

## The code (single file, no DI)

```csharp
using Repl;
using System.ComponentModel;

var store = new ContactStore(); // simple in-memory store

CoreReplApp.Create()
    .UseDefaultInteractive()
    .Map("list",
        [Description("List all contacts")]
        () => store.List())

    .Map("add {name} {email}",
        [Description("Add a contact")]
        ([Description("Full name")] string name,
         [Description("Email address")] string email) =>
        {
            var contact = store.Add(name, email);
            return contact;
        })

    .Map("show {id:int}",
        [Description("Show a contact by id")]
        (int id) => store.Show(id))

    .Map("count",
        [Description("Count contacts")]
        () => store.Count())

    // Hidden utility for demos/tests
    .Map("debug reset",
        [Browsable(false)]
        () => store.Reset())

    .Run(args);
```

### Key things to notice

- **No command classes**. Routes are mapped directly to delegates.
- **Routes define the shape**: `add {name} {email}`, `show {id:int}`.
- **Types matter**:
  - `{id:int}` enforces an integer at parse time (also inferred from parameter type)
  - `{email}` can use an `email` constraint if you enable it.
- **Attributes are reused**:
  - `[Description]` feeds help output
  - `[Browsable(false)]` hides a command from discovery.
- **Return values are semantic**:
  - `IEnumerable<Contact>` → table
  - `Contact` → structured output (or JSON with `--json`)
  - `string` → plain text.

---

## Agent-style discovery (zero prior knowledge)

An automated client can do this:

```text
$ myapp --help
Commands:
  list
  add {name} {email}
  show {id}
  count
```

```text
$ myapp add --help
Usage: add {name} {email}

Arguments:
  name    Full name
  email   Email address
```

From this alone, an agent can:
- discover the available commands,
- understand required arguments,
- and call them with `--json` to get structured results.

No screen scraping. No custom schema. No extra endpoints.

---

## Notes and limitations

- This sample uses an **in-memory store**.  
  Each CLI invocation is a new process, so sequences in the CLI examples are a narrative shortcut.
- The `debug reset` command exists for demos/tests but is **hidden from help** via `[Browsable(false)]`.
- This demo **does not** use:
  - dynamic scoping,
  - completion providers,
  - middleware,
  - named options,
  - or custom output formats.

Those come next.

---

## What’s next?

Now that you’ve seen:
- routes,
- constraints,
- help/discovery,
- and dual CLI/REPL execution,

the next demo introduces **scopes and navigation**:

👉 [**02 — Scoped Contacts**](../02-scoped-contacts/): enter contexts, navigate with `..`, and see how the command graph becomes *stateful* without turning into a shell you have to invent yourself.
```
