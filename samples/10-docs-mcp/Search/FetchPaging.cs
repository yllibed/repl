namespace DocsMcpSample.Search;

internal static class FetchPaging
{
	internal static FetchedDocument Create(string id, string documentId, string title, string source,
		string text, int maxChars, int offset = 0, string? nextId = null, string generation = "")
	{
		if (offset < 0 || offset > text.Length)
		{
			throw new ArgumentException($"Use a UTF-16 offset between 0 and {text.Length}.", nameof(offset));
		}
		if (offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]))
		{
			throw new ArgumentException("The offset must not split a UTF-16 surrogate pair.", nameof(offset));
		}

		// The fetch command validates maxChars (100..20,000), so each page makes progress.
		var end = offset + Math.Min(text.Length - offset, maxChars);
		if (end > offset && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) { end--; }
		var truncated = end < text.Length;
		return new(id, source, title, text[offset..end], truncated, generation,
			offset, truncated ? end : null,
			!truncated && !string.Equals(id, documentId, StringComparison.Ordinal) ? nextId : null, documentId);
	}
}
