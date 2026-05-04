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

| Flag | Meaning |
|---|---|
| `--result:page-size <n>` or `--result:page-size=<n>` | Requested page size. Clamped to `ResultFlowOptions.MaxPageSize`. |
| `--result:cursor <value>` or `--result:cursor=<value>` | Opaque continuation cursor. |
| `--result:all` | Signals that the caller wants all rows. Handler decides whether this is allowed. |
| `--result:pager=auto|off|more|scroll|external` | Pager preference for human formats. |

Current pager behavior is implemented by the integrated pager. `external` is accepted as a forward-compatible mode and currently falls back to the integrated pager.

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
- the rendered payload has more lines than the visible row capacity;
- pager mode is not `off`.

Supported keys:

| Key | Behavior |
|---|---|
| `Space` / `PageDown` / any unhandled key | Next page. |
| `Enter` / `DownArrow` | Next line. |
| `UpArrow` | Re-display one previous line window. |
| `PageUp` | Re-display previous page window. |
| `q` / `Esc` | Quit paging. |

The v1 pager is intentionally conservative. It is closer to `more` than a full-screen `less`: it does not own an alternate screen, does not search, and does not launch external processes.

## MCP Behavior

MCP tools expose two reserved input properties on every tool schema:

| Property | Meaning |
|---|---|
| `_replCursor` | Continuation cursor from a previous paged result. |
| `_replPageSize` | Requested page size for the tool call. |

These properties are consumed by the Repl MCP adapter and mapped to `IReplPagingContext`. They are not forwarded as command business options.

When a handler returns `ReplPage<T>`, MCP returns:

- `StructuredContent`: the full `{ items, pageInfo }` envelope.
- `Content`: a short text summary such as `Returned 1 item(s). Total: 2. Continue with _replCursor=page-2.`

This keeps agents from receiving a giant JSON string in `TextContentBlock` while still preserving structured data for clients that support it.

## Spectre Behavior

`Repl.Spectre` renders `ReplPage<T>` with the same lightweight Spectre table style used for collections, followed by continuation metadata. The core paging contract remains framework-neutral; handlers do not need Spectre-specific code.

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
    options.Output.ResultFlow.ProgrammaticMaxInlineBytes = 64 * 1024;
});
```

| Option | Default | Meaning |
|---|---:|---|
| `DefaultPageSize` | `100` | Used when no caller or terminal hint provides a better size. |
| `MaxPageSize` | `1000` | Maximum accepted page size. |
| `ReservedVisibleRows` | `2` | Rows reserved for prompts/status when computing visible data rows. |
| `DefaultPagerMode` | `Auto` | Default pager behavior for human formats. |
| `ProgrammaticMaxInlineBytes` | `65536` | Reserved for programmatic inline-size policy. |

## Implementation Notes

- Existing handlers that return `IEnumerable<T>` keep their current behavior.
- Handlers that can page efficiently should request `IReplPagingContext` and return `ReplPage<T>`.
- `IReplPageSource<T>` is available for renderer-driven paging providers; current renderers use explicit `ReplPage<T>` results.
- `--result:all` is advisory. Handlers should reject or cap it when the data source cannot safely return everything.
- The pager operates after formatting. It does not fetch additional data pages by itself in v1.

## See Also

- [Output System](output-system.md)
- [Command Reference](commands.md)
- [MCP Reference](mcp-reference.md)
- [Interaction](interaction.md)
