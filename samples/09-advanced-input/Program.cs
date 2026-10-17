using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Repl;

var index = new SearchIndex();
var app = ReplApp.Create(services =>
	{
		services.AddSingleton(index);
		services.AddSingleton(new SearchPolicy(MaximumLimit: 5));
	})
	.WithDescription("Advanced input: adapt a legacy CLI inside a larger application.")
	.WithBanner("Type demo for a walkthrough. Try /find \"hello world\" 3, then press Tab.")
	.UseDefaultInteractive()
	.UseCliProfile();

app.UseArgumentPreprocessor((input, ct) =>
{
	ct.ThrowIfCancellationRequested();
	if (input.Arguments is ["/find"])
	{
		return ValueTask.FromResult(ArgumentPreprocessingResult.Continue(
			InputValidationResult.Incomplete("Supply a search term: /find \"hello world\" [limit].")));
	}

	if (input.Arguments is ["/find", var text])
	{
		return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", text]));
	}

	if (input.Arguments is ["/find", var query, var limit])
	{
		return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", query, "--limit", limit]));
	}

	return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
});

app.UseArgumentPreprocessor((input, ct) =>
{
	ct.ThrowIfCancellationRequested();
	if (input.Arguments is not ["/refresh"])
	{
		return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
	}

	// The surrounding application owns this action; typing and completion must not perform it.
	if (input.Mode != InputProcessingMode.Execution ||
		input.Purpose is InputPurpose.Help or InputPurpose.Completion)
	{
		return ValueTask.FromResult(ArgumentPreprocessingResult.Handled());
	}

	return ValueTask.FromResult(ArgumentPreprocessingResult.Handled(index.Refresh()));
});

app.UseInputValidator((ParsedInputContext input, SearchPolicy policy, CancellationToken ct) =>
{
	ct.ThrowIfCancellationRequested();
	if (input.Command != "query {text}" || !input.TryGetValue<int>("limit", out var limit))
	{
		return InputValidationResult.Valid();
	}

	return limit >= 1 && limit <= policy.MaximumLimit
		? InputValidationResult.Valid()
		: InputValidationResult.Invalid($"The result limit must be between 1 and {policy.MaximumLimit}.");
});

app.Map("query {text}",
	[Description("Search the in-memory application index")]
	(string text, SearchIndex search, int limit = 3) => search.Query(text, limit));

app.Map("status",
	[Description("Show how often the host action has run in this process")]
	(SearchIndex search) => $"Host index refreshes: {search.RefreshCount}.");

app.Map("demo",
	[Description("Show the advanced-input walkthrough")]
	() => """
		1. Type /find and pause: the input is incomplete.
		2. Type /find "hello world" 3 and press Tab: it becomes query "hello world" --limit 3.
		3. Press Enter: the normal query handler runs.
		4. Submit /find "hello world" 9: the injected SearchPolicy rejects the limit.
		5. Submit status, then type /refresh and pause: no refresh runs while typing.
		6. Submit /refresh, then status: the host refresh count increases once; the session stays open.
		""");

return await app.RunAsync(args);

internal sealed record SearchPolicy(int MaximumLimit);

internal sealed class SearchIndex
{
	private readonly string[] _entries = ["hello world", "hello world from Repl", "hello world from the host", "legacy CLI", "input validation"];
	private int _refreshCount;

	public int RefreshCount => Volatile.Read(ref _refreshCount);

	public string Refresh() => $"Host index refreshed ({Interlocked.Increment(ref _refreshCount)}).";

	public string Query(string text, int limit)
	{
		var matches = _entries.Where(entry => entry.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit);
		return string.Join(Environment.NewLine, matches.DefaultIfEmpty("No matches."));
	}
}
