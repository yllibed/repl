# Result Flow And Paging

Result flow is the layer between handler execution and output formatting. It lets commands avoid returning unbounded result sets, gives handlers a page-size hint, and lets each output surface choose the safest delivery behavior.

This is separate from output format selection. `--json`, `--human`, `--spectre`, and other formats still control serialization and rendering. Result flow controls how much data is returned and whether an interactive pager is used.

## Goals

- Avoid flooding terminal output with very large handler results.
- Preserve Unix pipe behavior: `| less`, `| more`, `| grep`, `| tail`, and file redirection must receive normal stdout data.
- Give handlers enough context to page at the source instead of loading everything.
- Return MCP results as small, structured pages instead of huge text blocks.
- Keep `Repl.Core` dependency-free and let richer packages such as `Repl.Spectre` adapt the same contracts.

## Handler Paging Context

Handlers can request `IReplPagingContext` as an injected parameter:

```csharp
app.Map("contacts", async (IReplPagingContext paging, ContactStore store, CancellationToken ct) =>
{
    var rows = await store.QueryAsync(
        cursor: paging.Cursor,
        take: paging.SuggestedPageSize,
        ct);

    return paging.Page(
        rows.Items,
        nextCursor: rows.NextCursor,
        totalCount: rows.TotalCount);
});
```

The context exposes:

| Member | Meaning |
|---|---|
| `VisibleRowCapacityHint` | Best-effort number of data rows the current surface can show. Null for redirected/programmatic surfaces. |
| `SuggestedPageSize` | Page size after applying caller options, terminal hints, and `ResultFlowOptions.MaxPageSize`. |
| `MaxPageSize` | Application maximum page size. |
| `Cursor` | Opaque continuation cursor supplied by the caller. |
| `AllRequested` | True when the caller passed `--result:all`. Handlers decide whether to honor it. |
| `Surface` | `Console`, `Interactive`, `Hosted`, `Redirected`, or `Programmatic`. |
| `Page<T>(...)` | Creates a `ReplPage<T>` result. |
| `CreateSource<T>(...)` | Creates an `IReplPageSource<T>` for renderer-driven future paging. |

`VisibleRowCapacityHint` is a hint, not a contract. Handlers may use it to tune `take`, but should still enforce their own data-source limits.

For interactive human output, prefer a page source when the data source can fetch
later pages. The integrated pager can then continue in the same command run
instead of asking the user to rerun with a cursor.

For in-memory data:

```csharp
app.Map("contacts", (ContactStore store) =>
    ReplPageSource.FromItems(store.List()));
```

For offset-based stores:

```csharp
app.Map("contacts", (ContactStore store) =>
    ReplPageSource.FromOffset<ContactRow>(
        (offset, take, ct) => store.QueryAsync(offset, take, ct),
        totalCount: store.Count));
```

For replayable async streams:

```csharp
app.Map("logs", (LogStore store) =>
    ReplPageSource.FromAsyncEnumerable(ct => store.StreamAsync(ct)));
```

Helpers also have state overloads so handlers can use static lambdas instead of
capturing local variables:

```csharp
app.Map("contacts", (ContactStore store) =>
    ReplPageSource.FromOffset<ContactRow, ContactStore>(
        store,
        static (state, offset, take, ct) => state.QueryAsync(offset, take, ct),
        totalCount: store.Count));
```

When a data source cannot apply a filter server-side, helpers can apply a
client-side filter before the final page is emitted:

```csharp
app.Map("contacts", (ContactStore store, string search) =>
    ReplPageSource.FromOffset<ContactRow, ContactStore>(
        store,
        static (state, offset, take, ct) => state.QueryAsync(offset, take, ct),
        filter: (_, row) => row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
```

Client-side filtering is a fallback, not the preferred path. Repl may fetch and
discard source rows while it fills one visible page, and the true filtered total
count is usually unknown unless the handler computes it separately. Prefer
pushing filters, search terms, tenant constraints, and sorting into the data
source whenever possible.

For opaque cursors, API tokens, and keyset paging, use `CreateSource<T>(...)`:

```csharp
app.Map("contacts", (IReplPagingContext paging, ContactStore store) =>
    paging.CreateSource<ContactRow>(async (request, ct) =>
    {
        var rows = await store.QueryAsync(
            cursor: request.Cursor,
            take: request.PageSize,
            ct);

        return request.Page(
            rows.Items,
            nextCursor: rows.NextCursor,
            totalCount: rows.TotalCount);
    }));
```

## Cursor Basics

A cursor is an opaque bookmark owned by the handler. Repl does not interpret it.
The handler consumes `request.Cursor` or `paging.Cursor`, and emits the next
bookmark as `nextCursor`.

The contract is:

```csharp
var currentCursor = request.Cursor;       // consume
var rows = await store.QueryAsync(currentCursor, request.PageSize, ct);
return request.Page(rows.Items, rows.NextCursor, rows.TotalCount); // emit
```

Rules of thumb:

- `null` or empty cursor means "first page".
- `nextCursor: null` means "there is no next page".
- `PageInfo.HasMore` is derived from `nextCursor` by the helpers.
- Treat incoming cursors as untrusted input. Validate and bound them.
- Prefer opaque, versioned cursor formats over exposing database internals.
- Include filters/sort/snapshot information in the cursor when changing those values would make the bookmark unsafe.

Use `ReplPage<T>` when the command returns one explicit page and expects callers
to pass `--result:cursor` for the next page. Use `IReplPageSource<T>` when human
users should continue interactively in the same run.

## Pagination Mode Matrix

| Mode | Cursor shape | What the handler/source needs | Best fit | Built-in helper |
|---|---|---|---|---|
| In-memory list | Offset string such as `25` | A bounded `IReadOnlyList<T>` | Samples, small cached data, tests | `ReplPageSource.FromItems(items)` |
| Async enumerable | Offset string such as `25` | A replayable `IAsyncEnumerable<T>` factory and cancellation-aware enumeration | Streams from files, SDK pagers exposed as async streams, tests | `ReplPageSource.FromAsyncEnumerable(ct => source.StreamAsync(ct))` |
| Offset/limit | Offset string such as `100` | Query by `offset` and `take`; ideally deterministic sort | SQL `Skip/Take`, search indexes, admin tables | `ReplPageSource.FromOffset((offset, take, ct) => ...)` |
| Page index | Page number or zero-based index | Query by page index and page size; agreement on one-based vs zero-based | APIs that already expose page numbers | Custom `CreateSource<T>` |
| Range/window | Encoded range, for example `2026-01-01..2026-01-31` | Stable ordering and a next range boundary | Time-series, logs, reporting windows | Custom `CreateSource<T>` |
| Keyset/seek | Last sort key, often encoded JSON | Deterministic sort and unique tie-breaker | Large mutable tables | Custom `CreateSource<T>` |
| Opaque cursor | Signed/encrypted bookmark | Cursor encoder/decoder and validation | Multi-tenant data, private filters, versioned cursors | Custom `CreateSource<T>` |
| External API token | Provider page token | API client that accepts a page token and returns the next token | REST/Graph/Cloud SDK paging | Custom `CreateSource<T>` |
| External nextLink | Provider URL | Validation that the URL belongs to the expected API | APIs that return full continuation links | Custom `CreateSource<T>` |
| Snapshot cursor | Snapshot id plus offset/key | Snapshot creation and cleanup policy | Consistent reports over changing data | Custom `CreateSource<T>` |

`FromOffset` and `FromAsyncEnumerable` fetch one extra matching item (`pageSize + 1`)
to detect whether another page exists without requiring a total count. When a
total is cheap and represents the final result set, pass it to `FromOffset` so
human output can show "Showing x of y". If the total is expensive, unknown, or
not meaningful for the current feed, leave it null.

Offset-style helpers are intentionally simple. They re-read or re-skip from the
start for later pages. For deep paging, mutable datasets, or live infinite
streams, prefer keyset, range, or an external provider cursor. Live feeds that
never finish are a separate use case; do not expose them through `--result:all`
or an unbounded in-memory list.

## Cursor Patterns

### Offset Cursor

Offset cursors are simple and work well for stable, append-only, or demo data.
They are not ideal for frequently changing result sets because inserts/deletes
can shift rows between requests.

For a store that can query by offset and take:

```csharp
app.Map("events", (EventStore store) =>
    ReplPageSource.FromOffset<EventRow>(
        (offset, take, ct) => store.QueryAsync(offset, take, ct),
        totalCount: store.Count));
```

For the common in-memory version:

```csharp
app.Map("events", (EventStore store) =>
    ReplPageSource.FromItems(store.AllEvents));
```

For a replayable async stream:

```csharp
app.Map("events", (EventStore store) =>
    ReplPageSource.FromAsyncEnumerable(ct => store.StreamAsync(ct)));
```

`FromAsyncEnumerable` passes the request cancellation token to the stream factory
and uses `WithCancellation(...)` while enumerating. It requires a replayable,
idempotent, and deterministic factory because later pages reopen the stream and
skip to the requested offset. For live streams or changing result sets that
cannot restart with the same ordering and contents, emit a keyset/range cursor
instead or use a future live/tail-oriented API.

Do not pass a channel, database cursor, network cursor, or shared enumerator
instance to `FromAsyncEnumerable`. Those are single-use streams. Use
`ReplPageSource.Create(...)` and emit an opaque source-owned cursor instead.

When you author the async iterator, accept cancellation with
`[EnumeratorCancellation]`:

```csharp
using System.Runtime.CompilerServices;

public async IAsyncEnumerable<LogRow> StreamAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var row in sdk.ReadLogsAsync(ct).WithCancellation(ct))
    {
        yield return row;
    }
}
```

### Keyset Cursor

Keyset cursors are better for databases and changing result sets. The cursor
contains the last row's sort key, not a row offset.

```csharp
using System.Text.Json;

record EventCursor(DateTimeOffset CreatedAt, long Id);

app.Map("events", (IReplPagingContext paging, EventDb db) =>
    paging.CreateSource<EventRow>(async (request, ct) =>
    {
        var cursor = DecodeCursor(request.Cursor);
        var query = db.Events.AsQueryable();

        if (cursor is not null)
        {
            query = query.Where(e =>
                e.CreatedAt > cursor.CreatedAt
                || (e.CreatedAt == cursor.CreatedAt && e.Id > cursor.Id));
        }

        var rows = await query
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Take(request.PageSize)
            .Select(e => new EventRow(e.Id, e.CreatedAt, e.Summary))
            .ToListAsync(ct);

        var nextCursor = rows.Count == request.PageSize
            ? EncodeCursor(new EventCursor(rows[^1].CreatedAt, rows[^1].Id))
            : null;

        return request.Page(rows, nextCursor);
    }));

static string EncodeCursor(EventCursor cursor) =>
    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));

static EventCursor? DecodeCursor(string? cursor) =>
    string.IsNullOrWhiteSpace(cursor)
        ? null
        : JsonSerializer.Deserialize<EventCursor>(Convert.FromBase64String(cursor));
```

For production, consider signing or encrypting cursor payloads when they contain
tenant ids, filters, or other sensitive data.

### External API Page Token

Many APIs already expose a page token. Pass Repl's cursor through to that API,
then emit the API's next token.

```csharp
app.Map("incidents", (IReplPagingContext paging, IncidentApi api) =>
    paging.CreateSource<IncidentRow>(async (request, ct) =>
    {
        var response = await api.SearchAsync(
            pageSize: request.PageSize,
            pageToken: request.Cursor,
            ct);

        return request.Page(
            response.Items,
            nextCursor: response.NextPageToken,
            totalCount: response.TotalCount);
    }));
```

### External API Next Link

Some APIs return a full `nextLink` URL instead of a token. The cursor can be that
link, as long as the handler validates that it belongs to the expected API.

```csharp
app.Map("messages", (IReplPagingContext paging, MailApi api) =>
    paging.CreateSource<MessageRow>(async (request, ct) =>
    {
        var response = string.IsNullOrWhiteSpace(request.Cursor)
            ? await api.ListMessagesAsync(request.PageSize, ct)
            : await api.GetNextPageAsync(request.Cursor, ct);

        return request.Page(response.Items, response.NextLink);
    }));
```

### Snapshot Cursor

When users expect a consistent report, include a snapshot id or timestamp in the
cursor. The first request creates the snapshot, later requests continue inside it.

```csharp
record AuditCursor(string SnapshotId, int Offset);

app.Map("audit", (IReplPagingContext paging, AuditStore store) =>
    paging.CreateSource<AuditRow>(async (request, ct) =>
    {
        var cursor = DecodeAuditCursor(request.Cursor)
            ?? new AuditCursor(await store.CreateSnapshotAsync(ct), Offset: 0);

        var page = await store.ReadSnapshotAsync(
            cursor.SnapshotId,
            cursor.Offset,
            request.PageSize,
            ct);

        var nextCursor = page.HasMore
            ? EncodeAuditCursor(cursor with { Offset = cursor.Offset + page.Items.Count })
            : null;

        return request.Page(page.Items, nextCursor, page.TotalCount);
    }));
```

## Result Page Shape

`ReplPage<T>` contains:

- `Items`: the current page.
- `PageInfo.Cursor`: cursor used for the current page.
- `PageInfo.NextCursor`: cursor for the next page.
- `PageInfo.TotalCount`: optional total count.
- `PageInfo.PageSize`: effective page size.
- `PageInfo.HasMore`: true when `NextCursor` is present.

JSON output uses a clean automation envelope:

```json
{
  "$type": "page",
  "items": [
    { "id": 1, "name": "Alice" }
  ],
  "pageInfo": {
    "cursor": "start",
    "nextCursor": "page-2",
    "totalCount": 42,
    "pageSize": 1,
    "hasMore": true
  }
}
```

The technical properties used by the renderer, such as `ItemType` and `UntypedItems`, are not serialized.

## CLI Flags

Result-flow flags are global and use the `--result:` prefix so they do not collide with command options such as `--limit` or `--cursor`.
The reserved names are also exposed in `ReplResultFlowOptionNames` so hosts can
avoid collisions when composing custom command surfaces.

| Flag | Meaning |
|---|---|
| `--result:page-size <n>` or `--result:page-size=<n>` | Requested page size. Clamped to `ResultFlowOptions.MaxPageSize`. |
| `--result:cursor <value>` or `--result:cursor=<value>` | Opaque continuation cursor. |
| `--result:all` | Signals that the caller wants all rows. Bounded helpers such as `FromItems` can honor it; unbounded helpers such as `FromOffset` and `FromAsyncEnumerable` reject it by default. |
| `--result:pager=auto\|off\|more\|inline\|full` | Pager preference for human formats. |

`auto` uses the full-screen alternate-buffer pager when ANSI rendering and key
input are available, then falls back to the simple `more` behavior in limited
terminals.

## Advanced: Source Paging Vs Output Paging

Result flow has two different paging layers. They are related, but they solve
different problems:

| Layer | What it pages | Who controls it | Why it exists |
|---|---|---|---|
| Source paging | Data items fetched by the handler or `IReplPageSource<T>` | The handler/source through `ReplPageRequest.PageSize` and `Cursor` | Avoid loading too many rows, expensive API calls, or unsafe memory growth. |
| Output paging | Rendered terminal lines already produced for human display | The interactive pager through `--result:pager` and terminal height | Avoid flooding the user's screen and provide navigation. |

Source paging happens before output formatting. Output paging happens after
formatting. A single data item can become zero, one, or many output lines,
depending on the formatter, terminal width, ANSI/Spectre rendering, wrapping,
and object shape.

For example, this source returns data pages:

```csharp
app.Map("activity", (IReplPagingContext paging, ActivityStore store) =>
    paging.CreateSource<ActivityRow>(async (request, ct) =>
    {
        var page = await store.QueryAsync(
            cursor: request.Cursor,
            take: request.PageSize,
            ct);

        return request.Page(
            page.Items,
            nextCursor: page.NextCursor,
            totalCount: page.TotalCount);
    }));
```

The source decides how many rows to fetch. The output pager decides how many
rendered lines to show before prompting:

```bash
myapp activity --result:page-size=100 --result:pager=more
```

This means:

- Repl asks the source for up to 100 `ActivityRow` items at a time.
- The human formatter turns those rows into a table.
- The `more` pager shows only the visible rendered lines, then waits for input.
- If the user pages past the buffered rows, Repl fetches the next source page
  using the source cursor.

Do not use output paging as a substitute for source paging. Returning a
100,000-row list and relying on the pager still allocates and formats the whole
list before the user sees the first screen. Use `IReplPageSource<T>` or
`ReplPage<T>` whenever the data source can page efficiently.

Do not use source paging as a substitute for output paging either. A source page
of 20 objects can still produce hundreds of terminal lines if rows contain long
strings, nested collections, or narrow-column wrapping. Human terminal output
still benefits from `auto`, `more`, `inline`, or `full`.

Rules of thumb:

- Tune `--result:page-size` for data-source cost, API limits, and memory safety.
- Tune `--result:pager` for terminal UX.
- Use `VisibleRowCapacityHint` only as a best-effort hint; it is not the same as
  `PageSize` and is not a hard display contract.
- For machine outputs, source paging still matters, but output paging is off.
- For redirected output, Repl writes normal stdout and lets tools such as
  `less`, `grep`, `tail`, or `jq` handle downstream paging/filtering.

## CLI And Pipe Behavior

The integrated pager only applies to human terminal formats:

- `human`
- `spectre`

It does not apply to machine formats:

- `json`
- `xml`
- `yaml`
- `markdown`

It also does not apply when stdout is redirected, when input cannot read keys, in MCP/programmatic execution, or during protocol passthrough.

This preserves standard shell behavior:

```bash
myapp contacts --human | less
myapp contacts --json | jq '.items[]'
myapp contacts --human | grep Alice
myapp contacts --human | tail -20
```

In those cases Repl writes the normal output stream and lets the receiving Unix tool do the paging/filtering.

## Integrated Pager

The integrated pager activates automatically when:

- the selected format is `human` or `spectre`;
- output is an interactive terminal or hosted session with key input;
- the rendered payload has more lines than the visible row capacity, or an
  `IReplPageSource<T>` reports another data page;
- pager mode is not `off`.

Supported keys:

| Key | Behavior |
|---|---|
| `Space` / `PageDown` / any unhandled key | Continue to the next screen, fetching the next data page when needed. |
| `Enter` / `DownArrow` | Next line. |
| `UpArrow` | Move up in `full` and `inline`; ignored by `more`. |
| `PageUp` | Move up one page in `full` and `inline`; ignored by `more`. |
| `q` / `Esc` | Quit paging. |

The integrated pager has two render paths:

- `more` fallback: writes page by page in the normal terminal buffer, never
  uses cursor movement, and only moves forward.
- `inline` viewport: redraws a controlled region in the normal terminal buffer
  with ANSI cursor movement.
- `full` viewport: enters the terminal alternate screen, keeps an internal line
  buffer, redraws a viewport explicitly, and leaves the original scrollback
  untouched when the user exits.

The full viewport is inspired by `less`: it does not depend on terminal
scrollback. It renders from an internal buffer and fetches additional
`IReplPageSource<T>` payloads as the user pages past the buffered end.

Applications that need a different terminal experience can register a custom
`IReplPagerRenderer` with
`options.Output.ResultFlow.UsePagerRenderer(renderer)`. A custom renderer is
selected by its `ReplPagerMode` and receives a `ReplPagerRenderContext`
containing the rendered payload, terminal writer, key reader, visible row hint,
and continuation fetcher.

The built-in `inline` and `full` pagers keep a bounded in-memory line buffer.
`MaxBufferedLines` defaults to `10_000`. When the limit is reached, Repl stops
fetching additional pages, keeps navigation inside the known content, and shows
a `buffer limit reached` status instead of growing memory indefinitely.

## Testing Result Flow

Test the cursor contract first. A page source can be exercised without a console:

```csharp
[TestMethod]
public async Task Contacts_ArePagedByCursor()
{
    var source = ReplPageSource.FromItems([
        new ContactRow(1, "Alice"),
        new ContactRow(2, "Bob"),
        new ContactRow(3, "Carla"),
    ]);

    var first = await source.FetchAsync(new ReplPageRequest(
        PageSize: 2,
        Cursor: null,
        VisibleRowCapacityHint: null,
        AllRequested: false,
        Surface: ReplResultSurface.Programmatic));

    var second = await source.FetchAsync(new ReplPageRequest(
        PageSize: 2,
        Cursor: first.PageInfo.NextCursor,
        VisibleRowCapacityHint: null,
        AllRequested: false,
        Surface: ReplResultSurface.Programmatic));

    first.Items.Select(c => c.Name).Should().Equal("Alice", "Bob");
    first.PageInfo.NextCursor.Should().Be("2");
    second.Items.Select(c => c.Name).Should().Equal("Carla");
    second.PageInfo.HasMore.Should().BeFalse();
}
```

For CLI JSON, assert the automation envelope:

```csharp
var output = CaptureConsole(() =>
    app.Run([
        "contacts",
        "--json",
        "--result:page-size=2",
        "--no-logo",
    ]));

var page = JsonSerializer.Deserialize<PageEnvelope<ContactRow>>(output.Text);
page!.Items.Should().HaveCount(2);
page.PageInfo.NextCursor.Should().NotBeNull();

public sealed record PageEnvelope<T>(
    IReadOnlyList<T> Items,
    ReplPageInfo PageInfo);
```

For MCP, call the generated tool twice. MCP uses `_replPageSize` and
`_replCursor`, and returns `pageInfo` in structured content:

```csharp
var first = await mcpClient.CallToolAsync(
    "contacts",
    new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["_replPageSize"] = 2,
    });

var firstRoot = first.StructuredContent!.Value;
var nextCursor = firstRoot
    .GetProperty("pageInfo")
    .GetProperty("nextCursor")
    .GetString();

var second = await mcpClient.CallToolAsync(
    "contacts",
    new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["_replPageSize"] = 2,
        ["_replCursor"] = nextCursor,
    });

second.StructuredContent!.Value
    .GetProperty("pageInfo")
    .GetProperty("cursor")
    .GetString()
    .Should().Be(nextCursor);
```

For Spectre CLI output, use the same command surface with the Spectre renderer
enabled. Assert content and, when ANSI is enabled, styling:

```csharp
var app = ReplApp.Create(services => services.AddSpectreConsole())
    .UseSpectreConsole();

app.Map("contacts", () => ReplPageSource.FromItems(rows));

var output = CaptureConsole(() =>
    app.Run([
        "contacts",
        "--spectre",
        "--result:page-size=2",
        "--result:pager=off",
        "--no-logo",
    ]));

output.Text.Should().Contain("Alice");
output.Text.Should().Contain("Next data page:");
```

For a Spectre TUI command, use Spectre prompts for selection workflows rather
than the result-flow pager. `SelectionPrompt<T>` and `MultiSelectionPrompt<T>`
support `.PageSize(...)` and `.MoreChoicesText(...)`, which is useful for
choosing an item from a page:

```csharp
var selected = AnsiConsole.Prompt(
    new SelectionPrompt<ContactRow>()
        .Title("Select a contact")
        .PageSize(10)
        .MoreChoicesText("[grey](Use arrows to see more contacts)[/]")
        .UseConverter(c => $"[bold]{c.Name}[/] [grey]{c.Email}[/]")
        .AddChoices(page.Items));
```

Use Spectre `Live(...)` for dashboards or dynamic refreshes. It is not a
replacement for a `less`-style pager, but it is a good fit for a TUI screen that
owns its render area.

## MCP Behavior

MCP tools expose two reserved input properties on every tool schema:

| Property | Meaning |
|---|---|
| `_replCursor` | Continuation cursor from a previous paged result. |
| `_replPageSize` | Requested page size for the tool call. |

These properties are consumed by the Repl MCP adapter and mapped to `IReplPagingContext`. They are not forwarded as command business options.
MCP and CLI cursors are expected to be compact opaque values, for example
base64url or another whitespace-free token. Repl rejects cursors that are empty,
contain whitespace or control characters, start with `-`, or exceed 512
characters before they can be converted to CLI tokens. MCP page-size values must
be numeric and at most 10 characters before normal result-flow clamping is
applied.
MCP arguments are also validated against the generated tool schema before they
are reconstructed as CLI tokens, so arbitrary JSON keys cannot inject global or
result-flow options.

When a handler returns `ReplPage<T>`, MCP returns:

- `StructuredContent`: the full `{ "$type": "page", items, pageInfo }` envelope.
- `Content`: a short text summary such as `Returned 1 item(s). Total: 2. Continue with _replCursor; cursor available in structured content.`

This keeps agents from receiving a giant JSON string in `TextContentBlock` while still preserving structured data for clients that support it.
Agents that preserve `StructuredContent` can continue by sending `_replCursor`
with the value from `pageInfo.nextCursor`. Agents that only read text receive a
safe fallback summary, but Repl does not place the raw cursor in text content.

## Spectre Behavior

`Repl.Spectre` renders `ReplPage<T>` with the same lightweight Spectre table style used for collections, followed by continuation metadata when the output is not being driven by the interactive pager. The core paging contract remains framework-neutral; handlers do not need Spectre-specific code.

The integrated pager still owns the final rendered text. Spectre live/full-screen surfaces should continue to capture or redirect regular Repl feedback as documented in [interaction.md](interaction.md#spectre-and-screen-ownership).

## Configuration

Configure through `ReplOptions.Output.ResultFlow`:

```csharp
app.Options(options =>
{
    options.Output.ResultFlow.DefaultPageSize = 100;
    options.Output.ResultFlow.MaxPageSize = 1000;
    options.Output.ResultFlow.ReservedVisibleRows = 2;
    options.Output.ResultFlow.DefaultPagerMode = ReplPagerMode.Auto;
    options.Output.ResultFlow.MaxBufferedLines = 10_000;
    options.Output.ResultFlow.ProgrammaticMaxInlineBytes = 64 * 1024;
});
```

| Option | Default | Meaning |
|---|---:|---|
| `DefaultPageSize` | `100` | Used when no caller or terminal hint provides a better size. |
| `MaxPageSize` | `1000` | Maximum accepted page size. |
| `ReservedVisibleRows` | `2` | Rows reserved for prompts/status when computing visible data rows. |
| `DefaultPagerMode` | `Auto` | Default pager behavior for human formats. |
| `MaxBufferedLines` | `10000` | Maximum content lines buffered by interactive viewport pagers. |
| `ProgrammaticMaxInlineBytes` | `65536` | Reserved for programmatic inline-size policy. |

Use `UsePagerRenderer(renderer)` to register one custom renderer per
`ReplPagerMode`. Registering another renderer for the same mode replaces the
previous one. `RemovePagerRenderer(mode)` and `ClearPagerRenderers()` are
available for test setup or host-specific composition.

## Diagnostics

`Repl.Core` exposes `IReplResultFlowDiagnostics` for dependency-free paging
diagnostics. Implementations receive page-fetch start, success, and failure
events with cursor and page-size metadata.

`Repl.Logging` registers a bridge automatically when `AddReplLogging()` is used:

- `Debug`: page fetch starting/succeeded.
- `Error`: page fetch failed, including the exception.

## Implementation Notes

- Existing handlers that return `IEnumerable<T>` keep their current behavior.
- Handlers that can page efficiently should request `IReplPagingContext` and return `ReplPage<T>`.
- Handlers that want human users to continue without rerunning the command should return `IReplPageSource<T>`.
- Non-interactive and machine outputs fetch the first source page and preserve the continuation cursor in the rendered page metadata.
- `--result:all` is advisory. Handlers should reject or cap it when the data source cannot safely return everything; built-in unbounded page-source helpers reject it by default.
- The pager operates after formatting for line navigation, and can fetch additional data pages when the handler returns `IReplPageSource<T>`.

## See Also

- [Core Basics sample](../samples/01-core-basics/README.md#result-flow-paging)
- [Spectre sample](../samples/07-spectre/README.md#activity--paged-long-data-source)
- [MCP Server sample](../samples/08-mcp-server/README.md#demo-workflow)
- [Output System](output-system.md)
- [Command Reference](commands.md)
- [MCP Reference](mcp-reference.md)
- [Interaction](interaction.md)
