using System.ComponentModel;
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
			docs.Map("search", ([Description("Question or terms: 1 to 2,000 characters; must also fit the model's 512-token query limit.")] string query, [FromServices] DocumentIndex index, [FromServices] IndexingWorker worker, CancellationToken ct, [ReplOption, Description("Number of results: 1 to 20, default 5.")] int count = 5) =>
			{
				worker.RequireReady();
				return index.Search(query, count, ct).Hybrid;
			})
				.WithDescription("Search the Markdown documentation by keywords and meaning. Fetch an identifier from the results to read its source.")
				.WithDetails("Use a result's id for its passage or documentId for the full page. Fetch the source before citing it.").ReadOnly();
			docs.Map("fetch", ([Description("An indexed passage id or documentId returned by search (console users can also use docs list); not a filesystem path.")] string id, [FromServices] DocumentIndex index, [FromServices] IndexingWorker worker, [ReplOption, Description("Maximum UTF-16 code units per page: 100 to 20,000, default 20,000.")] int maxChars = 20000,
				[ReplOption, Description("UTF-16 offset into this identifier: start at 0, then use nextOffset exactly. Must not split a surrogate pair.")] int offset = 0) =>
			{
				worker.RequireReady();
				return index.Fetch(id, maxChars, offset);
			})
				.WithDescription("Read an indexed document or passage using an identifier returned by search.")
				.WithDetails("Continue the same id using nextOffset. Once it is null, nextId identifies the next part under the same heading, if any; fetch it at offset 0. Null continuations end traversal. Each call reads the current generation; restart reading if generation changes.").ReadOnly();
			docs.Map("list", ([FromServices] DocumentIndex index) => ReplPageSource.FromOffset<DocumentSummary>(
				(offset, take, ct) => ValueTask.FromResult<IReadOnlyList<DocumentSummary>>(index.List(offset, take, ct))))
				.WithDescription("Browse indexed documents with Repl paging; use --result:page-size and --result:cursor for one-shot pages.").ReadOnly();
		}).WithDetails("Search and browse the published documentation.");
		map.Map("search compare", (string query, [FromServices] DocumentIndex index, CancellationToken ct, [ReplOption] int count = 5) => index.Search(query, count, ct))
			.WithDescription("Compare keyword, vector and hybrid rankings for one query.").ReadOnly();
	}
}
