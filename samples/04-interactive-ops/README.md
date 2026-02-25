# 04 — Interactive Ops
**Guided operations with prompts, progress, timeouts, and cancellation**

This demo is where a REPL stops feeling like “a command loop” and starts feeling like a **guided operator console**.

It exercises the full **`IReplInteractionChannel`** surface:

- text prompts (with retry-on-invalid),
- n-way choice prompts (default, prefix matching),
- confirmations (safe defaults),
- status messages,
- two progress models (**`IProgress<double>`** and **`IProgress<ReplProgressEvent>`**),
- prompt timeouts (with countdown → auto-default),
- and cancellation patterns (**Esc during prompts**, **Ctrl+C during commands**).

The goal: make interactive commands feel **production-ready**, while still remaining **scriptable** and **automation-friendly**.

---

## ⚡ 60-second tour

### Interactive transcript (guided flow)

```text
> contact import contacts.csv
Parsing 'contacts.csv'...
Detected 2 duplicate(s).
How to handle duplicates? [SKIP/overwrite/cancel]: ov
Importing: 100%
5 imported, 2 overwritten, 0 skipped.
```

This is not “print strings and hope”.  
These are **typed interaction events** rendered by the host.

---

### Non-interactive CLI (prefilled answers)

```text
$ myapp contact import contacts.csv --answer:duplicates=skip --output:json
{ "imported": 3, "overwritten": 0, "skipped": 2 }
```

Same command. Same handler.  
Interactive prompts become deterministic automation via `--answer:*`.

---

## What you are seeing

This sample makes **interaction** a first-class contract:

- Handlers publish **semantic events**:
  - `ReplStatusEvent`
  - `ReplProgressEvent`
  - `ReplPromptEvent`
- The **presenter/host** decides how to render them:
  - ANSI terminals → in-place updates, colors, richer UX
  - plain text/log hosts → append-only transcript

Your business logic stays clean and testable.  
Presentation stays configurable and host-specific.

> Handlers emit meaning, presenters decide appearance.

---

## Channel coverage (at a glance)

| Channel method                 | Example Command          | Pattern                                             |
|--------------------------------|--------------------------|-----------------------------------------------------|
| `AskTextAsync`                 | `add`                    | retry-on-invalid (loop)                             |
| `AskChoiceAsync`               | `import`                 | n-way choice + default + prefix match + 10s timeout |
| `AskConfirmationAsync`         | `clear`                  | safe default (`false`)                              |
| `WriteStatusAsync`             | `add`, `import`, `watch` | inline feedback                                     |
| `IProgress<ReplProgressEvent>` | `import`                 | structured progress (current/total)                 |
| `IProgress<double>`            | `sync`                   | simple percentage                                   |

Also demonstrated:

- Optional route parameters (`{name?}`, `{email?:email}`) → prompt for missing values
- `--answer:*` prefill for non-interactive automation and agents
- `Results.Cancelled()` for user-declined operations
- **Ctrl+C cancellation**: first cancels the running command, second exits the session (app)
- **Prompt timeout** via `AskOptions(Timeout: ...)` → countdown then auto-default
- **Long-running watch** pattern: runs until cancelled via cooperative cancellation

---

## Interactive flow (full transcript)

```text
> contact add
Contact name?: Alice Martin
Email address?: not-an-email
'not-an-email' is not a valid email address.
Email address?: alice@example.com
Contact 'Alice Martin' added.

> contact import contacts.csv
Parsing 'contacts.csv'...
Detected 2 duplicate(s).
How to handle duplicates? [SKIP/overwrite/cancel]: (10s → Skip)
Importing: 100%
3 imported, 0 overwritten, 2 skipped.

> contact watch
Watching... 4 contacts. (Ctrl+C to stop)
^C
Press Ctrl+C again to exit.
Cancelled.

> contact clear
Delete all 4 contact(s)? [y/N]: y
4 contact(s) removed.
```

---

## Two progress models (simple → structured)

### 1) Minimal progress: `IProgress<double>`

This is the simplest primitive: the operation emits a percentage, and the host decides how to render it.

```csharp
ops.Map("sync",
    async (ISyncService sync,
           IProgress<double> progress,
           CancellationToken ct) =>
{
    await sync.RunAsync(progress, ct);
    return Results.Ok("Sync completed.");
});
```

---

### 2) Structured progress: `IProgress<ReplProgressEvent>`

This keeps semantics in the operation (`Label`, `Current`, `Total`, `Percent`) and leaves rendering to the presenter.

```csharp
ops.Map("import {file}",
    async (string file,
           IImportService importer,
           IProgress<ReplProgressEvent> progress,
           CancellationToken ct) =>
{
    await foreach (var step in importer.ImportAsync(file, ct))
    {
        progress.Report(new ReplProgressEvent(
            Label: "Importing",
            Current: step.Current,
            Total: step.Total));
    }

    return Results.Ok("Import completed.");
});
```

---

## Scriptable prompts: `--answer:*` prefill

Interactive decisions can be made deterministic:

```text
$ myapp contact import contacts.csv --answer:duplicates=skip
Importing: 100%
3 imported, 0 overwritten, 2 skipped.
```

And machine output stays stable:

```text
$ myapp contact import contacts.csv --answer:duplicates=skip --output:json
{
  "imported": 3,
  "overwritten": 0,
  "skipped": 2
}
```

Notes:

- choice prefill matches labels case-insensitively
- confirmation prefill accepts `y/yes/true/1` and `n/no/false/0`

---

## Optional route parameters → prompt for missing values

This demo uses optional trailing segments as a natural companion to prompts:

- `{name?}` — optional string
- `{email?:email}` — optional constrained parameter
- optional segments must be **trailing**

Example pattern:

```csharp
contact.Map(
    "add {name?} {email?:email}",
    [Description("Add a contact (prompts for missing fields)")]
    async (string? name, string? email, IContactStore store,
           IReplInteractionChannel channel, CancellationToken ct) =>
    {
        while (string.IsNullOrWhiteSpace(name))
            name = await channel.AskTextAsync("name", "Contact name?");

        while (string.IsNullOrWhiteSpace(email)
            || !MailAddress.TryCreate(email, out _))
        {
            if (!string.IsNullOrWhiteSpace(email))
                await channel.WriteStatusAsync($"'{email}' is not a valid email address.", ct);

            email = await channel.AskTextAsync("email", "Email address?");
        }

        store.Add(new Contact(name, email));
        return Results.Success($"Contact '{name}' added.");
    });
```

Invocation styles:

- `contact add` → prompts for both
- `contact add "Alice"` → prompts for email only
- `contact add "Alice" alice@test.com` → no prompts
- `contact add --answer:name=Alice --answer:email=a@b.com` → deterministic, no prompts

Help displays this shape as: `add [name] [email]`.

---

## Cancellation contract

This demo shows multiple cancellation paths:

- **Ctrl+C during a command**: cancels the current command, session continues with “Cancelled.”
- **Ctrl+C again within ~2s** (or at a bare prompt): exits the session (app)
- **Esc during a prompt**: cancels the prompt (on terminals that support it)
- **Prompt timeout**: auto-selects default after countdown

Long-running commands (like `watch`) follow the cooperative pattern:
- inject `CancellationToken`
- stop when cancelled
- return `Results.Cancelled()` or equivalent

---

## What’s next?

You now have:

- a shared command surface (01),
- stateful navigation and DI (02),
- composable modules (03),
- and guided interaction patterns (04).

The next demo moves the same ideas into **remote sessions**:

👉 [**05 — Hosting Remote**](../05-hosting-remote/): sessions, transports, terminal metadata, and running the same REPL over WebSocket / Telnet carriers.
