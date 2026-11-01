# Rebuild without interrupting readers

## One worker, one published generation

The indexing worker scans Markdown files every sixty seconds and compares content hashes. Adding, editing or deleting a file requests a full rebuild. `index rebuild` requests the same operation immediately.

Embeddings and replacement rows are prepared in a staging database. Readers continue using the published generation. The worker publishes documents, keyword rows and vectors together in one SQLite transaction.

## Observe and cancel

`index watch` shows indexing activity in a live Spectre view. Ctrl+C leaves this view; it does not cancel indexing. `index cancel` requests cancellation of the rebuild itself.

A failed or cancelled rebuild preserves the previous index. Check `index status` for the last failure. When documents change while indexing, the generation is not published and a later scan retries.

## Start with a warm index

A compatible persistent index can serve queries while the worker checks the source files. Model identity and schema version are recorded with the generation so incompatible vectors are never mixed silently.
