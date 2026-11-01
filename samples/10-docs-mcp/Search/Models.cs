namespace DocsMcpSample.Search;

internal sealed record Section(string Id, string DocumentId, string Source, string Title, string Heading, string Anchor, int StartLine, int EndLine, string Text);
internal sealed record SourceDocument(string Id, string Source, string Title, string Text, IReadOnlyList<Section> Sections);
internal sealed record DocumentSummary(string Id, string Source, string Title);
internal sealed record SearchHit(string Id, string DocumentId, string Source, string Title, string Heading, string Anchor, int StartLine, int EndLine, string Excerpt, double Score);
internal sealed record SearchComparison(string Query, string Generation, IReadOnlyList<SearchHit> Keyword, IReadOnlyList<SearchHit> Vector, IReadOnlyList<SearchHit> Hybrid);
internal sealed record FetchedDocument(string Id, string Source, string Title, string Text, bool Truncated, string Generation);
internal sealed record IndexGeneration(string Id, string Fingerprint, string Model, int Documents, int Sections, DateTimeOffset PublishedAt);

internal interface ITextEmbedding
{
	string Identity { get; }
	int Dimensions { get; }
	int CountPassageTokens(string text);
	float[] Embed(string text, bool query, CancellationToken cancellationToken);
}
