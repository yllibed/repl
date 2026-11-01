# Document index administration

The interface is a working document desk: the index at the left, three readings of the same query across the page, and the live process underneath. The comparison of rankings is the signature, not decoration around operational counters.

The canonical tokens live in `wwwroot/app.css`: cool blue canvas (`--canvas`), white work surface (`--surface`), dark ink (`--ink`), and petrol (`--accent`). Warning and failure colors have separate semantic roles. System sans-serif carries navigation; system monospace carries identifiers, source references and console content. No external fonts or runtime CDN requests.

Desktop uses a narrow index rail and a flexible search surface. At 760px the rail stacks above the results and ranking columns become sections. The console keeps its own scroll area. Source text has a bounded reading area; the document itself remains scrollable. No decorative motion; the indexing activity indicator respects reduced motion.

The page is English, matching the samples. Search accepts multilingual text. Dates and numbers use the browser locale. Every interactive element uses native keyboard behavior and visible focus. Source content is inserted as text, never HTML or Spectre markup.

`app.js` owns search cancellation, source selection, status polling and feedback. `samples/shared/terminal-client.js` owns raw WebSocket terminal transport. xterm owns terminal input and rendering. Search is explicitly submitted; no background query on every keystroke. Clearing cancels pending work. Source selection cancels the previous fetch. A failed status poll retains the last snapshot and labels it stale.

Administration is deliberately unauthenticated. The warning remains visible above the workspace. Index actions use the same Repl invocation pipeline through a restricted admin endpoint and show their outcome beside the buttons. They work independently of an occupied or detached terminal. Console command helpers copy text rather than injecting it into a potentially active prompt. Disconnect and Ctrl+C never stop the indexing worker.

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
| --- | --- | --- | --- | --- |
| Form | Native form and `app.js` search controller | This document and `wwwroot/app.js` | Explicit search submission; clear cancels pending work | Browser search, clear and invalid-input checks |
| Scrollbar | Global application stylesheet | `wwwroot/app.css` | Source reader and xterm own their bounded scroll areas | Desktop and narrow viewport checks |
| Async feedback | `app.js` status regions | This document and `wwwroot/app.js` | Poll snapshot, search result, command outcome | Browser loading, failure and reconnect checks |
| Terminal | Shared raw WebSocket client and xterm | `samples/shared/terminal-client.js` | Local console uses the remote CLI protocol | Session isolation and WebSocket command tests |
