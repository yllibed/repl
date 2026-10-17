# Advanced Input Adaptation and Validation

Most applications need only `Map` and `RunAsync`. The opt-in APIs on this page are
for compatibility with an existing command line, additional input validation, or
applications where Repl is a small part of a larger system.

Use normal option definitions, route constraints and context validation first.
Input adapters are useful when the old interface cannot be expressed through
ordinary routes or option aliases.

## Translate an Existing Command Line

Register adapters on the root `ReplApp` or `CoreReplApp`. They run before strict
parsing and routing. Each adapter receives a read-only snapshot and returns an
explicit decision: `Continue`, `Replace` or `Handled`.

```csharp
using Repl;

var app = ReplApp.Create();

app.UseArgumentPreprocessor((input, ct) =>
{
    ct.ThrowIfCancellationRequested();

    if (input.Arguments is ["/find", var query])
    {
        return ValueTask.FromResult(
            ArgumentPreprocessingResult.Replace(["query", query]));
    }

    return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
});

app.Map("query {text}", (string text) => text);
return await app.RunAsync(args);
```

For example, `/find "hello world"` becomes the argument array
`["query", "hello world"]`. Argument boundaries are retained; adapters do not
build a shell command string. Repl still applies its normal parsing, binding and
command/context validation to the translated invocation.

Multiple adapters run in registration order. `Arguments` contains the preceding
adapter's output; `OriginalArguments` retains the initial input. A replacement
does not restart the adapter chain. The interactive `ScopeTokens` are separate
from the editable arguments and must not be prepended by an adapter.

To choose a default startup command, replace an empty invocation:

```csharp
app.UseArgumentPreprocessor((input, _) => ValueTask.FromResult(
    input.Mode == InputProcessingMode.Execution && input.Arguments.Count == 0
        ? ArgumentPreprocessingResult.Replace(["serve"])
        : ArgumentPreprocessingResult.Continue()));
```

These callbacks run before input parsing, after the current hosting path has
provided its services. They do not provide the before-DI preparation lifecycle
described in [issue #114](https://github.com/yllibed/repl/issues/114).

## Preview, Completion and External Actions

Adapters run during typing as well as submission. `Mode` distinguishes:

| Mode | Request |
| --- | --- |
| `Execution` | A submitted startup or interactive invocation |
| `Preview` | A live analysis while the user edits input |
| `Completion` | An explicit interactive completion request |
| `ShellCompletion` | The user command line transported by a shell bridge |

`Purpose` identifies a command, help request or completion request when recognized.
It can be `Unknown` before an adapter has interpreted application-specific syntax.
Help can have `Mode == Execution`; checking only the mode does not exclude help.

The application is responsible for filtering external actions. A callback that
writes files, starts a service or performs a migration must guard that work:

```csharp
app.UseArgumentPreprocessor((input, _) =>
{
    if (input.Arguments is not ["/external"])
    {
        return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
    }

    if (input.Mode != InputProcessingMode.Execution ||
        input.Purpose is InputPurpose.Help or InputPurpose.Completion)
    {
        return ValueTask.FromResult(ArgumentPreprocessingResult.Handled());
    }

    // Perform the application's external action here.
    return ValueTask.FromResult(ArgumentPreprocessingResult.Handled("Action completed."));
});
```

`Handled` stops later adapters and normal command dispatch. During preview and
completion it never renders the returned payload or exits the session. On
submission it uses normal result rendering and exit-code classification.
Input validators do not run after a handled decision, including in the editor.
Handling an interactive invocation keeps the session open; `Results.Exit(code)`
explicitly closes it. Application callback failures are application errors, not
invalid user input. Caller cancellation follows the existing cancellation policy.

Live analysis can run repeatedly, overlap with an older canceled analysis, and be
discarded when the text changes. Keep callbacks fast and observe their cancellation
token. Repl cannot undo effects a callback performs before checking its mode.

## Validate Parsed Values

Use `UseInputValidator` for checks involving multiple input values or application
rules. It accepts synchronous, `Task` and `ValueTask` delegates returning
`InputValidationResult`. `ParsedInputContext`, services from the current scope and
`CancellationToken` are injected directly into the signature.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Repl;

var app = ReplApp.Create(services => services.AddSingleton(new LimitPolicy(100)));
app.Map("query", (int limit) => $"Requested {limit} results.");

app.UseInputValidator((ParsedInputContext input, LimitPolicy policy) =>
{
    if (input.Command != "query")
    {
        return InputValidationResult.Valid();
    }

    if (!input.TryGetValue<int>("limit", out var limit))
    {
        return InputValidationResult.Incomplete("Supply a result limit.");
    }

    return limit <= policy.Maximum
        ? InputValidationResult.Valid()
        : InputValidationResult.Invalid($"The maximum is {policy.Maximum}.");
});

record LimitPolicy(int Maximum);
```

Validators run in registration order and see diagnostics returned by earlier
validators. They run during preview, completion and submission, so services that
perform expensive or external checks should use `input.Input.Mode` to decide
when those checks are appropriate. Returning `Incomplete` communicates that a
check or value is still needed. Both `Incomplete` and `Invalid` prevent normal
execution when an invocation is submitted.

Interactive ambient commands, including `help`, `?`, `history`, `exit` and custom
ambient commands, also pass through application validators before dispatch. A
refused `exit` leaves the session open. Ambient commands have no resolved route
or command values; inspect `input.Input.Arguments` to identify them.

The snapshot exposes command values through `Values` and globals through
`GlobalValues`. Each value is `Available`, `Missing` or `Invalid`; typed values
are converted using Repl's existing conversion rules. A partial command may not
have a resolved `Command`. Missing values must not be treated as zero or false.
Only input values and literal parameter defaults are analyzed: preview does not
instantiate options groups or construct the handler's service dependencies.

During preview, DI-enabled hosts consult `IServiceProviderIsService`; custom
providers should expose that metadata too. A registration may return `null`, in
which case binding falls back to positional input or a literal default. When
positional assignment or a required value depends on that result, preview returns
`Incomplete` and omits the affected values until submission. It does not activate
the factory to guess the assignment.

At submission, implicit service fallbacks for input parameters are resolved before
input validators run. Their results, including `null`, are reused by the handler's
binder so factories run once and validators see the actual positional assignment.
Non-null services are omitted from parsed input values. Named input still takes
precedence and does not activate that parameter's fallback. Explicit
`[FromServices]` dependencies and options groups remain deferred until binding.
Failures while resolving a handler fallback retain the normal binding-error
format, classification and exit policy. Exceptions from input validators remain
callback failures.

Built-in syntax, type and route-constraint checks remain in place. Existing
context validators and command middleware remain execution steps; they are not
automatically invoked on every keystroke. Input validation does not replace
authorization or final context validation.

## Diagnostics and Conversion on Tab

An incomplete quote, missing value or enum prefix that can still become valid
can be shown as `Incomplete`. Invalid input produces diagnostics in the live hint
and at submission. Adapters can also return a validation result with their
`Continue` or `Replace` decision.

Diagnostics can include an `InputSourceRange` in the original text. Replacement
arguments may supply an `origins` entry for each effective argument. Unchanged
argument objects retain an unambiguous origin automatically; synthesized values
need explicit ranges for precise highlighting. Unmapped diagnostics describe the
whole invocation rather than highlighting an unrelated token.

By default, the first Tab on valid adapted input converts the line into equivalent
Repl syntax. Subsequent Tab presses use ordinary completion. Repl does not
automatically convert incomplete or invalid input. To keep the old syntax:

```csharp
app.Options(options =>
    options.Interactive.Autocomplete.NormalizeInputOnTab = false);
```

Validation still analyzes the translated arguments. Suggestions are projected
back to the original text only when an adapter's origin information permits a
safe replacement. An unmappable edit is omitted rather than inserting a token at
the wrong position. Shell bridges continue using their shell-specific completion
protocol; they do not replace the whole command line as the Repl editor does.

For adapted shell input, suggestions are omitted when the original command prefix
contains quotes or shell escapes, or when its source mapping is unavailable.
This conservative check also omits suggestions after an earlier, closed quoted
argument: shell escapes can carry quote context across apparent token boundaries.
Reformatting the arguments cannot establish safe insertion into the original line.

On the shell bridge, adaptation and input validation share one
`ShellCompletion.ProviderTimeout` budget (one second by default). This budget is
separate from the deadline for each completion provider. A callback failure or
timeout returns no suggestions and writes a short diagnostic to stderr; stdout
remains reserved for completion candidates. Caller cancellation propagates without
an error diagnostic. Overdue callbacks are canceled and abandoned, but application
code must still cooperate with cancellation to stop its work.

## Scope

This input pipeline covers CLI, local and hosted interactive input, and shell
completion. No adapter or validator is enabled by default. Programmatic and MCP
interception belongs to [issue #115](https://github.com/yllibed/repl/issues/115);
structured requests are not converted into command-line text by these APIs.

Repl provides the adaptation points, not a second declarative grammar engine.
Applications remain responsible for translating their previous interface.
