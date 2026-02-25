# 03 — Modular Ops
**Composable modules, generic CRUD, and per-entity stores**

This demo takes the next step: instead of hand-writing every command tree, you **compose** your command surface from **reusable modules** and mount the same module under **multiple contexts**.

The result:

- one **generic CRUD module** reused for several entities,
- **open-generic DI** to provide a store per entity type,
- **entity adapters** to keep the UX human-friendly,
- and a **single interactive session** that can run multi-step workflows without losing state.

Think of this as the moment your command surface becomes **architectural** rather than just a list of routes.

---

## ⚡ 30-second tour

### The setup

- A generic module: `EntityCrudModule<TEntity>`
- Mounted three times:
  - `client ...`
  - `contact ...`
  - `invoice ...`
- Each entity gets its own store via **open-generic DI**:
  - `IEntityStore<Client>`
  - `IEntityStore<Contact>`
  - `IEntityStore<Invoice>`
- Small **entity adapters** keep names, labels, and messages friendly while the CRUD stays generic.

---

### One-shot CLI

```text
$ myapp ops ping
pong
```

```text
$ myapp client add "ACME Corp" --json
{ "id": 1, "name": "ACME Corp" }
```

```text
$ myapp client list --json
[{ "id": 1, "name": "ACME Corp" }]
```

Same module.  
Different entity.  
Same command surface.

---

### Interactive CRUD flow (same process, same store)

```text
$ myapp

> client add "ACME Corp"
Client 'ACME Corp' added.

> client 1 show
Id    1
Name  ACME Corp

> client 1 update "ACME Canada"
Client updated.

> client 1 remove
Client removed.

> exit
```

This is not a series of disconnected processes.  
It’s one **stateful session** running a **multi-step narrative**.

---

## What you are seeing

This sample introduces **composition as a first-class concept**:

- **Modules** define reusable command sets.
- The **same module** is mounted under multiple contexts.
- **Open-generic DI** provides a different store per entity type.
- **Entity adapters** customize names, labels, and messages without duplicating CRUD logic.
- A **single interactive session** keeps state across multiple commands.
- CLI and REPL still use the **same command graph**.

You are no longer authoring commands one by one.  
You are **assembling** a command surface from building blocks.

---

## The command model (mental picture)

```text
myapp
├── ops/
│   └── ping
├── client/
│   ├── list
│   ├── add {name}
│   └── {id}/
│       ├── show
│       ├── update {name}
│       └── remove
├── contact/
│   └── (same CRUD shape)
└── invoice/
    └── (same CRUD shape)
```

- The **shape** is identical.
- The **entity type** changes.
- The **store implementation** changes via DI.
- The **module code** stays the same.

---

## The code (modules + open generics)

At a high level:

```csharp
var app = ReplApp.Create(services =>
{
    // Open-generic store registration:
    services.AddSingleton(typeof(IEntityStore<>), typeof(InMemoryEntityStore<>));
})
.UseDefaultInteractive()
.UseCliProfile();

// Mount the same CRUD module three times:
app.Context("client", m => m.MapModule<EntityCrudModule<Client>>());
app.Context("contact", m => m.MapModule<EntityCrudModule<Contact>>());
app.Context("invoice", m => m.MapModule<EntityCrudModule<Invoice>>());

// Ops module for non-CRUD commands
app.Context("ops", m => m.MapModule<OpsModule>());

return app.Run(args);
```

Inside `EntityCrudModule<TEntity>`, you define **once**:

- `list`
- `add`
- `{id} show`
- `{id} update`
- `{id} remove`

The framework:

- binds `{id}` from the route,
- injects `IEntityStore<TEntity>` from DI,
- and renders results using the same output pipeline as before.

---

### Key things to notice

- **Modules** are mounted with `MapModule<TModule>()`.
- The **same module** is reused for multiple entities.
- **Open-generic DI** (`IEntityStore<TEntity>`) provides per-entity storage.
- Handlers receive **route values + DI services** together.
- A **single interactive session** can run a multi-step CRUD workflow.
- CLI and REPL still execute the **same commands**.

---

## Agent-style usage

From an agent’s perspective, nothing special is required:

```text
$ myapp --help
Commands:
  ops
  client
  contact
  invoice
```

```text
$ myapp client --help
Commands:
  list
  add {name}
  {id} ...
```

```text
$ myapp client 1 show --json
{ "id": 1, "name": "ACME Corp" }
```

The agent doesn’t care that this comes from a module.  
It just sees a **consistent, discoverable command surface**.

---

## What this demo adds over 02

- **Module composition** with `MapModule<TModule>()`
- **Generic command sets** reused across multiple contexts
- **Open-generic DI** for per-entity services
- **Entity adapters** to keep UX friendly without duplicating logic
- **Multi-step interactive workflows** in a single session
- A clear separation between **command shape** and **domain type**

---

## Notes and limitations

- This sample focuses on **composition and reuse**.
- It does **not** demonstrate:
  - completion providers,
  - middleware,
  - aliases.

Those come later.

---

## What’s next?

You now have:

- a shared command surface (01),
- stateful navigation and DI (02),
- and composable modules (03).

The next demo moves into **interactive guidance**:

👉 [**04 — Interactive Ops**](../04-interactive-ops/): prompts, confirmations, progress, timeouts, and cancellation — turning commands into guided workflows for humans *and* automation.
