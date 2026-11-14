namespace DocsMcpSample.Search;

internal static class FetchPaging
{
	internal static FetchedDocument Create(string id, string source, string title, string text, string generation,
		int maxChars, int offset, string? nextId, string documentId)
	{
		if (maxChars is < 100 or > 20000) { throw new ArgumentOutOfRangeException(nameof(maxChars), "Choose 100 to 20,000 characters."); }
		if (offset < 0 || offset > text.Length)
		{
			throw new ArgumentException($"Use a UTF-16 offset between 0 and {text.Length}.", nameof(offset));
		}
		if (TextBoundary.BeforeSurrogatePair(text, offset) != offset)
		{
			throw new ArgumentException("The offset must not split a UTF-16 surrogate pair.", nameof(offset));
		}

		var end = TextBoundary.BeforeSurrogatePair(text, offset + Math.Min(text.Length - offset, maxChars));
		var truncated = end < text.Length;
		return new(id, source, title, text[offset..end], truncated, generation,
			offset, truncated ? end : null,
			!truncated ? nextId : null, documentId);
	}
}
