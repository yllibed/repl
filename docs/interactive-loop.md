# Interactive Loop

The interactive loop is the heart of a REPL session. It renders a prompt, reads input, tokenizes it, resolves the target command within the current scope, and executes it — repeating until the user exits.

## Entering Interactive Mode

There are two ways to start an interactive session:

1. **Empty arguments with `UseDefaultInteractive()`** — When the app is launched with no arguments and `UseDefaultInteractive()` is configured, the app enters interactive mode automatically.
2. **Returning `Results.EnterInteractive()`** — Any handler can return this result to transition from CLI mode into an interactive session.

```csharp
var app = new ReplApp();
app.UseDefaultInteractive(); // enter interactive mode when no args provided

app.Map("setup", () => Results.EnterInteractive()); // explicit transition
```

## Session Lifecycle

`RunInteractiveSessionAsync` drives the loop:

1. Render the prompt (including the current scope path).
2. Read a line of input with autocompletion.
3. Tokenize the raw input string into arguments.
4. Resolve the command against the current scope.
5. Execute the command through the pipeline.
6. Repeat until exit.

## Prompt and Autocompletion

The prompt displays the current scope path. As the user types, the autocompletion system suggests available commands, contexts, and option names visible from the current scope. Tab completion resolves candidates from the command graph relative to `scopeTokens`.

## Input Tokenization

Raw input is split into tokens following standard shell conventions: whitespace-delimited, with quoted strings preserved as single tokens. This produces the same argument array that CLI mode receives from the OS shell.

## Scope Navigation

Interactive sessions maintain a **scope** — the current position in the command graph hierarchy. The scope is tracked as a `scopeTokens` list representing the path from the root.

### Entering a context

Type the context name (plus any required route values) to navigate into it:

```text
> user 42
user(42)>
```

### Going back

Type `..` to move up one level:

```text
user(42)> ..
>
```

### Scope state management

The `scopeTokens` list is prepended to every command entered while in a scope. When the user types `show` inside scope `user 42`, the resolved tokens become `["user", "42", "show"]`. The `..` navigation command mutates `scopeTokens` directly without executing a handler.

## Ambient Commands

Ambient commands are built-in commands available in every scope during interactive mode. They are not part of the command graph.

| Command    | Description                        |
|------------|------------------------------------|
| `exit`     | End the interactive session.       |
| `history`  | Show command history.              |
| `clear`    | Clear the terminal screen.         |
| `help`     | Display help for the current scope.|
| `complete` | List completions for input.        |

### Custom ambient commands

Register additional ambient commands via `AmbientCommandOptions.MapAmbient()`:

```csharp
app.ConfigureAmbientCommands(o =>
{
    o.MapAmbient("ping", () => "pong");
});
```

Custom ambient commands follow the same resolution rules — they take priority over graph commands with the same name.

## History Management

The interactive loop records each executed input line in a history buffer. The `history` ambient command prints past entries. Arrow-key navigation (up/down) recalls previous inputs at the prompt. History is maintained per session and is not persisted to disk by default.

## Cancel Key Handling

Pressing **Ctrl+C** during input clears the current line and returns to the prompt. During command execution, Ctrl+C triggers the `CancellationToken` passed to the handler, allowing cooperative cancellation. It does not terminate the interactive session.

## See Also

- [Commands](commands.md) — defining commands and contexts.
- [Configuration Reference](configuration-reference.md) — `InteractiveOptions` settings.
- [Execution Pipeline](execution-pipeline.md) — how commands are resolved and executed.
