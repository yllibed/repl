# Retrieval guide

## Search by words

Keyword retrieval uses SQLite FTS5. Query terms are joined with OR, so a passage must match at least one term to enter that candidate list. A small collection does not make unrelated passages lexical matches. Titles and headings receive more weight than body text.

## Search by meaning

Vector retrieval uses the local multilingual E5 model and exact cosine nearest neighbors. It can find passages without shared words, including across languages. The nearest passage is still only a candidate, not proof that it answers the question.

## Compare the rankings

Use `search compare` or the dashboard to see keyword, vector and hybrid rankings separately. Each branch has a candidate budget of at least twenty passages, or four times the requested result count. Displayed lists are limited to the requested count; agreement between lists is allowed.

## Read the source

A result includes a passage identifier, document identifier, source path, heading and line range. Fetch the passage for focused evidence, or its document identifier for the original page. Check the returned generation before combining readings across a rebuild.

## Maintain the corpus

Keep documentation focused and current. Give passages useful titles and headings rather than repeating keywords unnaturally. The worker detects Markdown additions, edits and removals. Search results change only when a complete replacement index has been published.

## Evaluate a change

Choose fixed questions and expected source passages before changing chunking or retrieval. Include exact-term, paraphrase, cross-language and unrelated queries. Compare rankings with the real model; adding documents or heading context does not guarantee better answers.
