namespace DocsMcpSample.Search;

internal sealed class IndexUnavailableException(string message) : InvalidOperationException(message);
