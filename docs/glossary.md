# Glossary

## Terms

### Ambient command

Built-in command available in interactive mode (e.g., `exit`, `history`, `help`, `complete`). Can be extended via `AmbientCommandOptions.MapAmbient()`.

### Argument binding

Process of resolving handler parameter values from route values, options, DI services, and positional arguments.

### Channel

Runtime execution mode: `Cli`, `Interactive`, `Session`, or `Programmatic`.

### Command graph

The complete set of routes, contexts, and handlers registered in an app.

### CommandBuilder

Fluent API returned by `app.Map()` for adding metadata to commands.

### Context

Named scope in the command graph that can contain nested commands. Created via `app.Context()`.

### CoreReplApp

Zero-dependency app class from `Repl.Core`. No DI or interactive defaults.

### Dynamic segment

Route template parameter in braces: `{name}` or `{name:type}`.

### Global option

Option parsed before routing, available to all commands (e.g., `--help`, `--output:format`).

### Handler

Delegate that implements a command's logic.

### IReplHost

Interface for hosting remote REPL sessions (WebSocket, Telnet).

### IReplInteractionChannel

Interface for bidirectional prompts, progress, and confirmations between handler and host.

### IReplModule

Interface for packaging reusable command groups.

### IReplSessionState

Per-session state container for interactive and hosted sessions.

### Literal segment

Static text in a route template matched exactly.

### MCP

Model Context Protocol. Allows AI agents to discover and invoke commands.

### MCP App

MCP UI extension that lets a command open a `ui://` HTML resource. Repl maps this with `.AsMcpAppResource()` on the HTML-producing command and returns launcher text for normal tool calls.

### Middleware

Pipeline function registered via `app.Use()` that wraps handler execution.

### Output transformer

Implementation of `IOutputTransformer` that converts results to a specific format.

### ReplApp

DI-enabled app class from `Repl.Defaults`. The recommended entry point.

### Response file

Text file referenced with `@filename` syntax, expanding to command-line arguments.

### Route template

String pattern defining a command's path, e.g., `"user {id:int} show"`.

### Route value

Value captured from a dynamic segment during route matching.

### Scope

Current position in the command graph hierarchy during an interactive session.
