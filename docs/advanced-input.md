# Advanced Input Adaptation and Validation

Most applications need only `Map` and `RunAsync`. The opt-in APIs on this page are
for compatibility with an existing command line, additional input validation, or
applications where Repl is a small part of a larger system.

Use normal option definitions, route constraints and context validation first.
Input adapters are useful when the old interface cannot be expressed through
ordinary routes or option aliases.

## Choose an Extension Point

| Need | Extension point | Stage |
| --- | --- | --- |
| Configure application objects or service registration from arguments | [Preparation hooks](advanced-startup.md#preparation-before-composition) | Explicit preparation before application composition |
| Perform an action using the run's services before dispatch | [Startup hooks](advanced-startup.md) | Once per run, after services are available |
| Translate legacy textual syntax | Raw argument adapter | Before strict parsing and routing |
| Inspect parsed values, redirect a request or handle an external action | Invocation hook | After preliminary parsing, before final validation |
| Accept or refuse input using parsed values | Input validator | Input analysis and submission, after invocation hooks when they run |
| Wrap ordinary command execution | Command middleware | After final command validation and binding |

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
        return ArgumentPreprocessingResult.Replace(["query", query]);
    }

    return ArgumentPreprocessingResult.Continue();
});

app.Map("query {text}", (string text) => text);
return await app.RunAsync(args);
```

`UseArgumentPreprocessor` accepts a synchronous callback returning
`ArgumentPreprocessingResult`, as above. An asynchronous overload with the signature
`Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>>`
is also available on `ReplApp`, `CoreReplApp` and `ReplStartupBuilder`. Use an `async`
lambda when the adapter needs to `await` asynchronous work.

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
app.UseArgumentPreprocessor((input, _) =>
    input.Mode == InputProcessingMode.Execution && input.Arguments.Count == 0
        ? ArgumentPreprocessingResult.Replace(["serve"])
        : ArgumentPreprocessingResult.Continue());
```

On ordinary runs, these callbacks run after the hosting path provides its services.
The explicit [startup preparation path](advanced-startup.md) can run declared
adapters before application composition and transfer them to later input handling.

## Intercept a Parsed Invocation

Use `UseInvocationHook` when a larger application needs to interpret parsed
parameters, handle an external action, or redirect an invocation. Like raw
adapters, this is an advanced integration API, principally for compatibility with
an existing interface. Ordinary commands should use normal handlers and options.

Hooks accept synchronous, `Task` and `ValueTask` delegates returning
`ReplInvocationResult`. The context, available typed command parameters, options
groups, registered typed global options, scoped services, keyed `[FromServices]`
dependencies and `CancellationToken` can be injected into the signature.

```csharp
app.UseInvocationHook((ReplInvocationContext context, SearchPolicy policy) =>
{
    if (context.Parsed.Command != "query {text}"
        || context.Parsed.Input.Purpose != InputPurpose.Command
        || !context.Parsed.TryGetValue<int>("limit", out var limit) || limit != 0
        || !context.Parsed.TryGetValue<string>("text", out var text))
    {
        return ReplInvocationResult.Continue();
    }

    return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand(
        "query {text}",
        new Dictionary<string, object?>
        {
            ["text"] = text,
            ["limit"] = policy.MaximumLimit,
        },
        context.Parsed.GlobalValues
            .Where(pair => pair.Value.State == InputValueState.Available)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Value)));
});
```

`OriginalRequest` retains the submitted request; `Request` reflects earlier
replacements. `Parsed` shares `ParsedInputContext` with validators: it includes
the resolved route when available, typed values with `Missing`/`Invalid` states,
globals, diagnostics, mode and purpose. Hooks run **before final validation** and
can repair malformed or partial input. Use a context-based signature when that
input may be invalid. A required typed hook parameter that is unavailable produces
a binding diagnostic; it does not silently receive a default numeric value.
This also applies to options-group properties declared by the resolved command
schema with an explicit required arity (`ExactlyOne` or `OneOrMore`); omitted
optional properties retain their initializers. A hook-only options group does not
add parameters to the command schema.

`Continue` advances to the next hook. `Replace` replaces the whole request,
including its globals; explicitly carry forward the values you want to preserve.
Repl reparses the replacement before the next hook, without replaying earlier
hooks, raw adapters or startup callbacks. Normal routing, constraints, arity,
validators, binding and middleware still apply to the final request.
`Handled(result)` skips later hooks and command execution and uses normal result
rendering. In an interactive session it leaves the session open unless the result
explicitly requests exit.

`Channel` distinguishes CLI, local interactive, hosted interactive, programmatic
and MCP input. This distinction does not add a new module-presence channel. Hooks
run on submission, help and explicit completion (Tab or the shell bridge), **not**
on background preview. Check purpose before external actions. Completion handling
suppresses candidates without rendering a payload or requesting session exit.
The shell bridge invokes hooks on its user-command payload, once, rather than on
the transport envelope. Structured replacements do not normalize the editor to a
route template; edits without a safe source mapping are omitted.

`CoreReplApp` supports the same hooks without DI packages. Capture application
objects in the delegate, use context and cancellation directly, and use registered
Core global-options factories. `ReplApp` additionally resolves application
services from the current run/session provider, including caller-owned providers.

## Submit Native Values

The same request type can be passed to `RunAsync`:

```csharp
var request = ReplInvocationRequest.ForCommand("search",
    new Dictionary<string, object?>
    {
        ["layers"] = new[] { "raw", "indexed" },
        ["limit"] = 3,
    });
var exitCode = await app.RunAsync(request);
```

Use the exact registered route template and parameter/property names, rather than
CLI option aliases. Context names and inactive commands cannot be dispatched as
structured targets. Hooks may handle or replace the request before this check.
Route arguments, positional arguments and options share the values dictionary.
The `globalValues` argument accepts registered global names;
built-in flags such as `help` and `interactive`, and `output`, are also supported.
`FromArguments(tokens)` submits or replaces textual input. Both forms have the
usual `ReplApp` provider and host overloads.

Native command scalars and arrays bind directly; they are not serialized into a
shell command and reparsed. This preserves empty strings, option-looking strings,
numeric values independent of CLI culture, and explicit empty collections.
Compatible native arrays retain their rank and lower bounds. Collection arity
checks apply to declared collection parameters; scalar application objects are
not enumerated to count their contents.
Requests copy their value maps and arrays and clone JSON elements on creation
and submission. Other native objects, including concrete dictionaries, lists and
sets supplied as parameter values, retain their type and remain caller-owned;
their internal state is not cloned. Do not mutate app configuration while runs are active.

Command-backed MCP tools and resources use this same native path after schema
allowlisting and reserved-field validation. Typed arrays reach hooks and binding
without CLI token expansion. MCP retains its existing convention that an empty
client array means an omitted option, preserving its default. A hook can supply
an explicit empty collection in a replacement. Prompt prefills, paging metadata,
frozen-catalog module presence and forced JSON output remain transport-owned.
Core and MCP packages must use matching versions; incompatible invocation
contracts fail closed.

## Callback Order

For each submitted input, the order is:

1. Raw adapters for textual input, in registration order. An explicit handled or
   refusal decision remains terminal.
2. Startup callbacks when applicable, once per run/session, followed by owned
   hosted-service startup. See [startup preparation](advanced-startup.md) for
   the earlier before-composition stages.
3. Preliminary global parsing, route resolution and typed diagnostics.
4. Invocation hooks, in registration order, with fresh analysis after replacement.
5. Final parsing and input validation, then handler binding, context validation,
   middleware, handler and result rendering.

For a run with prepared input or startup hooks, raw adaptation precedes those
startup callbacks. Otherwise the normal hosting setup, including owned service
startup, finishes before per-input adaptation. Startup preparation can itself
refuse invalid globals before invocation hooks become available; use preparation
or startup hooks for decisions needed at that earlier stage.
For structured `RunAsync`, submission ownership and native globals are established
before owned hosted services start, including when no startup hook is registered.
Native globals are validated before that service startup; invocation hooks run
afterward and cannot repair globals rejected at this earlier stage.

Later interactive commands and nested MCP calls do not repeat startup. Native
structured requests bypass raw token adapters. Existing validators remain the
validation API; middleware wraps resolved command execution. Hook failures use
the callback-error outcome and caller cancellation follows normal policy.

A startup token replacement also replaces a submitted native request, so its
original native values cannot override that decision. `OriginalRequest` still
identifies the submitted request. Independent public `RunAsync` calls start with
their own globals and transport controls; a hook replacement within an MCP call
retains that call's transport-owned output and paging controls.

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
        return ArgumentPreprocessingResult.Continue();
    }

    if (input.Mode != InputProcessingMode.Execution ||
        input.Purpose is InputPurpose.Help or InputPurpose.Completion)
    {
        return ArgumentPreprocessingResult.Handled();
    }

    // Perform the application's external action here.
    return ArgumentPreprocessingResult.Handled("Action completed.");
});
```

`Handled` stops later adapters and normal command dispatch. During preview and
completion it never renders the returned payload or exits the session. On
submission it uses normal result rendering and exit-code classification.
Input validators do not run after a handled decision, including in the editor.
Handling an interactive invocation keeps the session open; `Results.Exit(code)`
explicitly closes it. Application callback failures are application errors, not
invalid user input: they are reported as `input_callback_error` in the format the
invocation asks for, as far as its input was processed (`--json` added by an earlier
adapter or by a request an earlier hook replaced included), and exit as a
`HandlerException`, unless that format is unknown,
which is the caller's usage error. Caller cancellation follows the existing
cancellation policy.

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

Input naming a context, at the prompt or on the command line, passes through
application validators before the context validators run: a refusal leaves an
interactive session in its current scope, and a command line never enters the
context. Such input has no resolved `Command` either, so a validator that
refuses every input without one also refuses navigation. To recognise it,
combine `input.Input.ScopeTokens` with `input.Input.Arguments`: at the prompt,
navigation is relative to the current scope, and routing also accepts unique
prefixes and other letter cases. Guard access to a context with its own
validation rather than by matching these words.

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

This input pipeline covers CLI, local and hosted interactive input, programmatic
requests, MCP and shell completion. No adapter, validator or invocation hook is
enabled by default. Legacy MCP invocations without invocation hooks retain their
existing validator behavior; enabling invocation hooks also enables final input
validation for that channel.

Repl provides the adaptation points, not a second declarative grammar engine.
Applications remain responsible for translating their previous interface.
