namespace DocsMcpSample.Search;

internal static class TextBoundary
{
	internal static int BeforeSurrogatePair(string text, int end) =>
		end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end - 1 : end;
}
