using DocsMcpSample.Search;
using Repl;
using Repl.Parameters;

namespace DocsMcpSample;

internal sealed class DocumentsModule : IReplModule
{
	public void Map(IReplMap map)
	{
		map.Context("docs", docs =>
		{
			docs.Map("search", (string query, [FromServices] DocumentIndex index, [FromServices] IndexingWorker worker, CancellationToken ct, [ReplOption] int count = 5) =>
			{
				worker.RequireReady();
				return index.Search(query, count, ct).Hybrid;
			})
				.WithDescription("Search the Markdown documentation by keywords and meaning. Fetch an identifier from the results to read its source.").ReadOnly();
			docs.Map("fetch", (string id, [FromServices] DocumentIndex index, [FromServices] IndexingWorker worker, [ReplOption] int maxChars = 20000) =>
			{
				worker.RequireReady();
				return index.Fetch(id, maxChars);
			})
				.WithDescription("Read an indexed document or section using an identifier returned by search. Reports when the text is truncated.").ReadOnly();
			docs.Map("list", ([FromServices] DocumentIndex index) => ReplPageSource.FromOffset<DocumentSummary>(
				(offset, take, ct) => ValueTask.FromResult<IReadOnlyList<DocumentSummary>>(index.List(offset, take, ct))))
				.WithDescription("Browse indexed documents with Repl paging; use --result:page-size and --result:cursor for one-shot pages.").ReadOnly();
		}).WithDetails("Search and browse the published documentation.");
		map.Map("search compare", (string query, [FromServices] DocumentIndex index, CancellationToken ct, [ReplOption] int count = 5) => index.Search(query, count, ct))
			.WithDescription("Compare keyword, vector and hybrid rankings for one query.").ReadOnly();
	}
}
