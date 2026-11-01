# A documentation server you can explore

This application indexes Markdown documents on your machine. It exposes two read-only MCP tools: search finds relevant sections and fetch reads the indexed source. No external AI service receives your documents or queries.

## Search by words and meaning

Keyword search uses SQLite FTS5. It is useful for exact API names, error messages and technical vocabulary. Vector search uses a local multilingual embedding model to find passages with a related meaning, even when their wording differs.

Hybrid search combines the two ranked lists using reciprocal rank fusion. A fusion score orders results; it is not a probability that an answer is correct.

## Read the source

Search results include document and section identifiers, relative source paths, headings and line numbers. Fetch a section to read a focused passage, or its document identifier to read the page. Fetch reads the published index, not an arbitrary path on disk.

## Bring your own documentation

Mount a folder of Markdown documents at `/documents`. The source volume is read-only. The `/index` volume stores the persistent SQLite database. Put both on local storage; this sample does not coordinate multiple writers.
