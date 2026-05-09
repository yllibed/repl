using System.Globalization;

namespace Repl;

internal static class ResultFlowPageFooterBuilder
{
	public static string RenderHuman(IReplPage page)
	{
		var info = page.PageInfo;
		var count = page.UntypedItems.Count;
		var countText = count.ToString(CultureInfo.InvariantCulture);
		if (info.TotalCount is { } total)
		{
			var prefix = $"Showing {countText} of {total.ToString(CultureInfo.InvariantCulture)}.";
			return info.HasMore
				? $"{prefix} Next data page: rerun with {ResultFlowCursorPolicy.FormatCliContinuation(info.NextCursor)}."
				: prefix;
		}

		return info.HasMore
			? $"Showing {countText} result(s). Next data page: rerun with {ResultFlowCursorPolicy.FormatCliContinuation(info.NextCursor)}."
			: string.Empty;
	}

	public static string RenderMarkdown(IReplPage page)
	{
		var info = page.PageInfo;
		var count = page.UntypedItems.Count.ToString(CultureInfo.InvariantCulture);
		if (info.TotalCount is { } total)
		{
			var prefix = $"Showing {count} of {total.ToString(CultureInfo.InvariantCulture)}.";
			return info.HasMore
				? $"{prefix} Continue with `{ResultFlowCursorPolicy.FormatCliContinuation(info.NextCursor)}`."
				: prefix;
		}

		return info.HasMore
			? $"Showing {count} result(s). Continue with `{ResultFlowCursorPolicy.FormatCliContinuation(info.NextCursor)}`."
			: string.Empty;
	}
}
