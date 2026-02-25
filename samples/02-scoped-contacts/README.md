# 02 — Scoped Contacts
**Dynamic scoping, stateful navigation, and DI-backed handlers**

This demo builds directly on **01 — Core Basics** and introduces three major ideas:

- **dynamic scopes** (`contact {name}`),
- **stateful navigation** in the REPL (with `..` and scope-aware prompts),
- and **dependency injection** for application services.

You can now **drill into a specific contact**, operate inside that scope, and let the framework manage context, navigation, and parameter binding for you.

> This is the point where your command surface stops being just a list of commands and becomes a navigable model.

---

## ⚡ 30-second tour

### One-shot CLI (fully qualified routes)

```text
$ myapp contact add "Alice Martin" alice@gmail.com
Contact 'Alice Martin' added.
```

```text
$ myapp contact "Alice Martin" show --json
{ "name": "Alice Martin", "email": "alice@gmail.com" }
```

```text
$ myapp contact list --markdown
# Contacts

| Name         | Email           |
| ------------ | --------------- |
| Alice Martin | alice@gmail.com |
```

Same handlers. Same return values.  
Only the **output format** changes.

---

### Interactive session (stateful scope)

```text
$ myapp

> contact add "Alice Martin" alice@gmail.com
Contact 'Alice Martin' added.

> contact "Alice Martin"
[contact/Alice Martin]> show
Name   Alice Martin
Email  alice@gmail.com

[contact/Alice Martin]> help
Commands:
  show
  remove

[contact/Alice Martin]> remove
Contact 'Alice Martin' removed.
> exit
```

No re-typing the contact name.  
The prompt shows **where you are**.  
Navigation is part of the model.

---

## What you are seeing

This sample introduces **scope as a first-class concept**:

- **Dynamic context segment**: `contact {name}`
- **Scope-aware prompt**: `[contact/Alice Martin]>`
- **Ambient navigation** with `..`
- **Command-driven navigation** via `Results.NavigateUp(...)`
- **DI-backed handlers** (`IContactStore` injected into commands)
- **Shape-driven rendering**:
  - a single `Contact` → key/value view,
  - a collection → table
- **Output format selection**:
  - `--json` for machine-readable output
  - `--markdown` for export-friendly output

Everything still runs from the **same command graph**.  
You are just adding **state and context** on top.

> Start REPL interactive mode by targeting it from CLI (`contact "Alice Martin"` → enters context)

---

## The command model (mental picture)

```text
myapp
└── contact/                        "Manage contacts"
    ├── list                        "List all contacts"
    ├── add {name} {email}          "Add a contact"
    └── {name}/                     "Manage a specific contact"
        ├── show                    "Show contact details"
        └── remove                  "Remove this contact"
```

- `{name}` is a **dynamic context**.
- Entering `contact "Alice Martin"` with no sub-command **enters the scope** in interactive mode.
- Inside that scope, only `show` and `remove` are visible.
- `help` and `--help` render **the same model** at every level.

---

## The code (DI + dynamic scope)

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IContactStore, InMemoryContactStore>();
})
    .WithDescription("Scoped contacts sample: dynamic contact scope with DI-backed storage.")
    .UseDefaultInteractive()
    .UseCliProfile();

app.Context(
    "contact",
    [Description("Manage contacts")]
    (IReplMap contact) =>
    {
        contact.WithBanner((TextWriter w, IContactStore store) =>
        {
            w.WriteLine($"  {store.All().Count} contact(s) in store. Try: list, add, remove");
        });

        // Root-level commands inside contact scope.
        contact.Map(
            "list",
            [Description("List all contacts")]
            (IContactStore store) => store.All());

        contact.Map(
            "add {name} {email:email}",
            [Description("Add a contact")]
            (string name, string email, IContactStore store) =>
            {
                store.Add(new Contact(name, email));
                return $"Contact '{name}' added.";
            });

        contact.Context(
            "{name}",
            [Description("Manage a specific contact")]
            (IReplMap scope) =>
            {
                // Scoped commands resolved for one selected contact name.
                scope.Map(
                    "show",
                    [Description("Show contact details")]
                    (string name, IContactStore store) => store.Get(name));

                scope.Map(
                    "remove",
                    [Description("Remove this contact")]
                    (string name, IContactStore store) =>
                    {
                        store.Remove(name);
                        return Results.NavigateUp($"Contact '{name}' removed.");
                    });
            },
            validation: (string name, IContactStore store) => store.Get(name) is not null);
    });

return app.Run(args);
```

### Key things to notice

- **Dynamic context** is declared with `Context("{name}", ...)`.
- The captured `{name}` value is **bound to handler parameters**.
- **DI services** (`IContactStore`) are injected alongside route values.
- **Validation** prevents entering a scope for a non-existing contact.
- `remove` returns **`Results.NavigateUp(...)`** to request navigation.
- **Route values take precedence over DI** when names collide. This means you can still enter a contact literally named `list` by using quotes: `"list"`.

---

## Agent-style discovery (zero prior knowledge)

An automated client can do this:

```text
$ myapp --help
Commands:
  contact    Manage contacts
```

```text
$ myapp contact --help
Commands:
  list                  List all contacts
  add {name} {email}    Add a contact
  {name} ...            Manage a specific contact
```

```text
$ myapp contact "Alice Martin" --help
Commands:
  show      Show contact details
  remove    Remove this contact
```

```text
$ myapp contact "Alice Martin" show
Name    Alice Martin
Email   alice@gmail.com
```

From this alone, an agent can:

- discover the `contact` context,
- understand that `{name} ...` is a **dynamic segment**,
- resolve it with a concrete value,
- and execute a scoped command — without ever entering an interactive session.

---

## What this demo adds over 01

- **Dynamic scopes** with `Context("{name}", ...)`
- **Stateful REPL navigation** with scope-aware prompts and `..`
- **Command-requested navigation** via `Results.NavigateUp(...)`
- **DI-backed handlers** with mixed binding (route values + services)
- **Single-object rendering** (key/value) vs **collection rendering** (tables)
- **Output format selection** with `--json` and `--markdown`
- **Help at every level**, including inside dynamic scopes
- **Agent-friendly discovery** of scoped commands via `--help`

---

## Notes and limitations

- This sample still uses an **in-memory store**.  
  As in demo 01, CLI sequences are a narrative shortcut.
- This demo **does not** use:
  - middleware,
  - named options,
  - or modules.

Those come next.

---

## What’s next?

Now that you’ve seen:

- dynamic scopes,
- stateful navigation,
- DI-backed handlers,
- and command-driven navigation,

the next demo introduces **module composition**:

👉 [**03 — Modular Ops**](../03-modular-ops/): reuse the same command sets across multiple contexts and start treating your command surface as a composable architecture.
```
