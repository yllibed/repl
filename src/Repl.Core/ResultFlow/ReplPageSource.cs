using System.Globalization;

namespace Repl;

/// <summary>
/// Convenience factories for result-flow page sources.
/// </summary>
public static class ReplPageSource
{
	private const int DefaultMaxSourceItemsToScan = 10000;

	/// <summary>
	/// Creates a page source from a fetch delegate.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="fetch">Delegate that fetches one page for each request.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> Create<T>(
		Func<ReplPageRequest, CancellationToken, ValueTask<ReplPage<T>>> fetch)
	{
		ArgumentNullException.ThrowIfNull(fetch);
		return new DelegateReplPageSource<T>(fetch);
	}

	/// <summary>
	/// Creates a page source from a fetch delegate and explicit state.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <typeparam name="TState">State type.</typeparam>
	/// <param name="state">State passed to the fetch delegate.</param>
	/// <param name="fetch">Delegate that fetches one page for each request.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> Create<T, TState>(
		TState state,
		Func<TState, ReplPageRequest, CancellationToken, ValueTask<ReplPage<T>>> fetch)
	{
		ArgumentNullException.ThrowIfNull(fetch);
		return Create<T>((request, cancellationToken) => fetch(state, request, cancellationToken));
	}

	/// <summary>
	/// Creates an offset-cursor page source over an in-memory list.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="items">Items to expose as pages.</param>
	/// <param name="filter">Optional client-side filter applied before final paging.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> FromItems<T>(
		IReadOnlyList<T> items,
		Func<T, bool>? filter = null)
	{
		ArgumentNullException.ThrowIfNull(items);

		return Create<T>((request, cancellationToken) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(CreateItemsPage(items, request, filter));
		});
	}

	/// <summary>
	/// Creates an offset-cursor page source over an in-memory list and explicit state.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <typeparam name="TState">State type.</typeparam>
	/// <param name="items">Items to expose as pages.</param>
	/// <param name="state">State passed to the filter delegate.</param>
	/// <param name="filter">Optional client-side filter applied before final paging.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> FromItems<T, TState>(
		IReadOnlyList<T> items,
		TState state,
		Func<TState, T, bool>? filter = null)
	{
		ArgumentNullException.ThrowIfNull(items);
		return FromItems(items, filter is null ? null : item => filter(state, item));
	}

	/// <summary>
	/// Creates an offset-cursor page source over a store that can fetch by offset and take.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="fetch">Delegate called with offset, take, and cancellation token.</param>
	/// <param name="totalCount">Total item count, when known without expensive enumeration.</param>
	/// <param name="filter">Optional client-side filter applied after source fetches and before final paging.</param>
	/// <param name="maxSourceItemsToScan">Maximum source rows to scan while filling one filtered page.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> FromOffset<T>(
		Func<int, int, CancellationToken, ValueTask<IReadOnlyList<T>>> fetch,
		long? totalCount = null,
		Func<T, bool>? filter = null,
		int? maxSourceItemsToScan = null)
	{
		ArgumentNullException.ThrowIfNull(fetch);
		return Create<T>((request, cancellationToken) =>
			CreateOffsetPageAsync(fetch, request, totalCount, filter, maxSourceItemsToScan, cancellationToken));
	}

	/// <summary>
	/// Creates an offset-cursor page source over a store that can fetch by offset and take, with explicit state.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <typeparam name="TState">State type.</typeparam>
	/// <param name="state">State passed to the fetch and filter delegates.</param>
	/// <param name="fetch">Delegate called with state, offset, take, and cancellation token.</param>
	/// <param name="totalCount">Total item count, when known without expensive enumeration.</param>
	/// <param name="filter">Optional client-side filter applied after source fetches and before final paging.</param>
	/// <param name="maxSourceItemsToScan">Maximum source rows to scan while filling one filtered page.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	public static IReplPageSource<T> FromOffset<T, TState>(
		TState state,
		Func<TState, int, int, CancellationToken, ValueTask<IReadOnlyList<T>>> fetch,
		long? totalCount = null,
		Func<TState, T, bool>? filter = null,
		int? maxSourceItemsToScan = null)
	{
		ArgumentNullException.ThrowIfNull(fetch);
		return FromOffset<T>(
			(offset, take, cancellationToken) => fetch(state, offset, take, cancellationToken),
			totalCount,
			filter is null ? null : item => filter(state, item),
			maxSourceItemsToScan);
	}

	/// <summary>
	/// Creates an offset-cursor page source over an async stream factory.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="createItems">Factory that creates the async stream for each page request.</param>
	/// <param name="filter">Optional client-side filter applied before final paging.</param>
	/// <param name="maxSourceItemsToScan">Maximum source rows to scan while filling one filtered page.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	/// <remarks>
	/// The factory must be replayable, idempotent, and deterministic for the same
	/// underlying result set: each page request reopens the stream and advances to the
	/// requested offset. Do not use this helper for single-use streams, live re-queries,
	/// mutable files, channels, network cursors, or shared enumerator instances. For
	/// those sources, use <see cref="Create{T}(Func{ReplPageRequest, CancellationToken, ValueTask{ReplPage{T}}})"/>
	/// with an opaque cursor owned by the source.
	/// <para>
	/// <b>Performance note:</b> fetching page N re-streams from the beginning and skips
	/// <c>(N-1) × pageSize</c> items; cost is O(offset) per page. For large or expensive
	/// sources prefer <see cref="Create{T}(Func{ReplPageRequest, CancellationToken, ValueTask{ReplPage{T}}})"/>
	/// with a source-native cursor so each page starts directly at the right position.
	/// </para>
	/// </remarks>
	public static IReplPageSource<T> FromAsyncEnumerable<T>(
		Func<CancellationToken, IAsyncEnumerable<T>> createItems,
		Func<T, bool>? filter = null,
		int? maxSourceItemsToScan = null)
	{
		ArgumentNullException.ThrowIfNull(createItems);
		return Create<T>((request, cancellationToken) =>
			CreateAsyncEnumerablePageAsync(createItems, request, filter, maxSourceItemsToScan, cancellationToken));
	}

	/// <summary>
	/// Creates an offset-cursor page source over an async stream factory, with explicit state.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <typeparam name="TState">State type.</typeparam>
	/// <param name="state">State passed to the stream factory and filter delegate.</param>
	/// <param name="createItems">Factory that creates the async stream for each page request.</param>
	/// <param name="filter">Optional client-side filter applied before final paging.</param>
	/// <param name="maxSourceItemsToScan">Maximum source rows to scan while filling one filtered page.</param>
	/// <returns>A page source consumable by Repl renderers.</returns>
	/// <remarks>
	/// The factory must be replayable, idempotent, and deterministic for the same
	/// underlying result set: each page request reopens the stream and advances to the
	/// requested offset. Do not use this helper for single-use streams, live re-queries,
	/// mutable files, channels, network cursors, or shared enumerator instances. For
	/// those sources, use <see cref="Create{T,TState}(TState, Func{TState, ReplPageRequest, CancellationToken, ValueTask{ReplPage{T}}})"/>
	/// with an opaque cursor owned by the source.
	/// <para>
	/// <b>Performance note:</b> fetching page N re-streams from the beginning and skips
	/// <c>(N-1) × pageSize</c> items; cost is O(offset) per page. For large or expensive
	/// sources prefer the stateful <see cref="Create{T,TState}"/> overload with a
	/// source-native cursor so each page starts directly at the right position.
	/// </para>
	/// </remarks>
	public static IReplPageSource<T> FromAsyncEnumerable<T, TState>(
		TState state,
		Func<TState, CancellationToken, IAsyncEnumerable<T>> createItems,
		Func<TState, T, bool>? filter = null,
		int? maxSourceItemsToScan = null)
	{
		ArgumentNullException.ThrowIfNull(createItems);
		return FromAsyncEnumerable<T>(
			cancellationToken => createItems(state, cancellationToken),
			filter is null ? null : item => filter(state, item),
			maxSourceItemsToScan);
	}

	private static ReplPage<T> CreateItemsPage<T>(
		IReadOnlyList<T> items,
		ReplPageRequest request,
		Func<T, bool>? filter)
	{
		var offset = request.AllRequested ? 0 : ParseOffset(request.Cursor);
		var filteredItems = filter is null
			? items
			: items.Where(filter).ToArray();
		var pageItems = request.AllRequested
			? filteredItems
			: filteredItems.Skip(offset).Take(request.PageSize).ToArray();
		var nextOffset = offset + pageItems.Count;
		var nextCursor = !request.AllRequested && nextOffset < filteredItems.Count
			? nextOffset.ToString(CultureInfo.InvariantCulture)
			: null;

		return request.Page(pageItems, nextCursor, filteredItems.Count);
	}

	private static async ValueTask<ReplPage<T>> CreateOffsetPageAsync<T>(
		Func<int, int, CancellationToken, ValueTask<IReadOnlyList<T>>> fetch,
		ReplPageRequest request,
		long? totalCount,
		Func<T, bool>? filter,
		int? maxSourceItemsToScan,
		CancellationToken cancellationToken)
	{
		ThrowIfAllRequestedForUnboundedSource(request);
		var offset = request.AllRequested ? 0 : ParseOffset(request.Cursor);
		var take = GetProbeSize(request.PageSize);
		if (filter is not null)
		{
			return await CreateFilteredOffsetPageAsync(
					fetch,
					request,
					offset,
					take,
					totalCount,
					filter,
					ResolveMaxSourceItemsToScan(maxSourceItemsToScan),
					cancellationToken)
				.ConfigureAwait(false);
		}

		var items = await fetch(offset, take, cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("The offset page source returned null.");
		return CreateOffsetProbePage(request, offset, items, totalCount);
	}

	private static async ValueTask<ReplPage<T>> CreateAsyncEnumerablePageAsync<T>(
		Func<CancellationToken, IAsyncEnumerable<T>> createItems,
		ReplPageRequest request,
		Func<T, bool>? filter,
		int? maxSourceItemsToScan,
		CancellationToken cancellationToken)
	{
		ThrowIfAllRequestedForUnboundedSource(request);
		var offset = request.AllRequested ? 0 : ParseOffset(request.Cursor);
		var pageItems = new List<T>(request.PageSize + 1);
		var scanned = 0;
		var index = 0;
		int? nextOffsetAfterVisible = null;
		var maxScan = ResolveMaxSourceItemsToScan(maxSourceItemsToScan);
		await foreach (var item in CreateStreamAsync(createItems, cancellationToken)
			.WithCancellation(cancellationToken)
			.ConfigureAwait(false))
		{
			if (index++ < offset)
			{
				continue;
			}

			ThrowIfScanLimitExceeded(scanned++, maxScan);
			if (filter is not null && !filter(item))
			{
				continue;
			}

			if (pageItems.Count == request.PageSize)
			{
				return request.Page(
					pageItems,
					nextOffsetAfterVisible?.ToString(CultureInfo.InvariantCulture));
			}

			pageItems.Add(item);
			nextOffsetAfterVisible = index;
		}

		return request.Page(pageItems);
	}

	private static ReplPage<T> CreateOffsetProbePage<T>(
		ReplPageRequest request,
		int offset,
		IReadOnlyList<T> items,
		long? totalCount)
	{
		var hasMore = items.Count > request.PageSize;
		var visibleItems = hasMore
			? items.Take(request.PageSize).ToArray()
			: items;
		var nextCursor = hasMore
			? (offset + visibleItems.Count).ToString(CultureInfo.InvariantCulture)
			: null;
		return request.Page(visibleItems, nextCursor, totalCount);
	}

	private static async ValueTask<ReplPage<T>> CreateFilteredOffsetPageAsync<T>(
		Func<int, int, CancellationToken, ValueTask<IReadOnlyList<T>>> fetch,
		ReplPageRequest request,
		int offset,
		int take,
		long? totalCount,
		Func<T, bool> filter,
		int maxSourceItemsToScan,
		CancellationToken cancellationToken)
	{
		var pageItems = new List<T>(request.PageSize);
		var currentOffset = offset;
		var scanned = 0;
		int? nextOffset = null;

		while (true)
		{
			var items = await fetch(currentOffset, take, cancellationToken).ConfigureAwait(false)
				?? throw new InvalidOperationException("The offset page source returned null.");
			if (items.Count == 0)
			{
				return request.Page(pageItems, totalCount: totalCount);
			}

			for (var index = 0; index < items.Count; index++)
			{
				ThrowIfScanLimitExceeded(scanned++, maxSourceItemsToScan);
				var item = items[index];
				if (!filter(item))
				{
					continue;
				}

				var sourceOffsetAfterItem = currentOffset + index + 1;
				if (pageItems.Count == request.PageSize)
				{
					var cursor = nextOffset ?? sourceOffsetAfterItem;
					return request.Page(pageItems, cursor.ToString(CultureInfo.InvariantCulture), totalCount);
				}

				pageItems.Add(item);
				nextOffset = sourceOffsetAfterItem;
			}

			if (items.Count < take)
			{
				return request.Page(pageItems, totalCount: totalCount);
			}

			currentOffset += items.Count;
		}
	}

	private static int GetProbeSize(int pageSize) =>
		pageSize == int.MaxValue ? pageSize : pageSize + 1;

	private static IAsyncEnumerable<T> CreateStreamAsync<T>(
		Func<CancellationToken, IAsyncEnumerable<T>> createItems,
		CancellationToken cancellationToken) =>
		createItems(cancellationToken)
		?? throw new InvalidOperationException("The async enumerable page source returned null.");

	private static int ParseOffset(string? cursor)
	{
		if (string.IsNullOrEmpty(cursor))
		{
			return 0;
		}

		if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
		{
			return offset;
		}

		throw new InvalidOperationException(
			$"The result cursor '{AbbreviateCursor(cursor)}' is not a valid non-negative offset cursor for this page source.");
	}

	private static string AbbreviateCursor(string cursor) =>
		cursor.Length <= 40 ? cursor : string.Concat(cursor.AsSpan(0, 40), "...");

	private static int ResolveMaxSourceItemsToScan(int? value) =>
		value is > 0 ? value.Value : DefaultMaxSourceItemsToScan;

	private static void ThrowIfAllRequestedForUnboundedSource(ReplPageRequest request)
	{
		if (request.AllRequested)
		{
			throw new InvalidOperationException(
				"--result:all is not supported by this page source because it could read an unbounded result set.");
		}
	}

	private static void ThrowIfScanLimitExceeded(int scanned, int maxSourceItemsToScan)
	{
		if (scanned >= maxSourceItemsToScan)
		{
			throw new InvalidOperationException(
				$"The client-side filter scan limit was reached before a complete page could be produced. Scanned {scanned.ToString(CultureInfo.InvariantCulture)} item(s); limit is {maxSourceItemsToScan.ToString(CultureInfo.InvariantCulture)}.");
		}
	}

	private sealed class DelegateReplPageSource<T>(
		Func<ReplPageRequest, CancellationToken, ValueTask<ReplPage<T>>> fetch) : IReplPageSource<T>
	{
		public ValueTask<ReplPage<T>> FetchAsync(
			ReplPageRequest request,
			CancellationToken cancellationToken = default) =>
			fetch(request, cancellationToken);
	}
}
