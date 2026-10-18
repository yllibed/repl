using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Repl;

var startup = ReplStartup.CreateBuilder().UseGlobalOptions<SampleStartupOptions>();
startup.UsePreparationHook((SampleStartupOptions options) => ArgumentPreprocessingResult.Continue(
	options.MaximumLimit > 0 ? InputValidationResult.Valid() : InputValidationResult.Invalid("MaximumLimit must be positive.")));

startup.UseArgumentPreprocessor((input, ct) =>
{
	ct.ThrowIfCancellationRequested();
	if (input.Arguments is ["/find"])
	{
		return ArgumentPreprocessingResult.Continue(
			InputValidationResult.Incomplete("Supply a search term: /find \"hello world\" [limit]."));
	}

	if (input.Arguments.Count >= 2 && input.Arguments[0] is "/find")
	{
		var text = input.Arguments[1];
		var trailing = input.Arguments.Skip(2).ToArray();
		return ArgumentPreprocessingResult.Replace(
			trailing.Length != 0 && !trailing[0].StartsWith("--", StringComparison.Ordinal)
				? ["query", text, "--limit", trailing[0], .. trailing.Skip(1)] : ["query", text, .. trailing]);
	}

	return ArgumentPreprocessingResult.Continue();
});

var prepared = await startup.PrepareAsync(args);
if (!prepared.CanContinue) { return await prepared.CompleteAsync(); }

var index = new SearchIndex();
var app = ReplApp.Create(prepared, services =>
	{
		services.AddSingleton(index);
		services.AddSingleton(new SearchPolicy(prepared.GetOptions<SampleStartupOptions>().MaximumLimit));
	})
	.WithDescription("Advanced input: adapt a legacy CLI inside a larger application.")
	.WithBanner("Type demo for a walkthrough. Try /find \"hello world\" 3, then press Tab.")
	.UseDefaultInteractive()
	.UseCliProfile();

app.UseStartupHook((ReplStartupContext context, SampleStartupOptions options, SearchIndex search) =>
{
	if (!options.RefreshOnStart || context.Purpose is InputPurpose.Help or InputPurpose.Completion)
	{
		return ArgumentPreprocessingResult.Continue();
	}
	var result = search.Refresh();
	return context.RemainingArguments.Count == 0
		? ArgumentPreprocessingResult.Handled(result) : ArgumentPreprocessingResult.Continue();
});

app.UseArgumentPreprocessor((input, _) =>
	input.Arguments is ["/refresh"] && input.Mode == InputProcessingMode.Preview
		? ArgumentPreprocessingResult.Handled() : ArgumentPreprocessingResult.Continue());

app.UseInvocationHook((ReplInvocationContext context, SearchIndex search, CancellationToken ct) =>
{
	ct.ThrowIfCancellationRequested();
	if (context.Request.Arguments is not ["/refresh"]) { return ReplInvocationResult.Continue(); }
	if (context.Parsed.Input.Purpose is InputPurpose.Help or InputPurpose.Completion)
	{
		return ReplInvocationResult.Handled();
	}
	return ReplInvocationResult.Handled(search.Refresh());
});

app.UseInvocationHook((ReplInvocationContext context, SearchPolicy policy) =>
{
	if (context.Parsed.Command != "query {text}" || context.Parsed.Input.Purpose != InputPurpose.Command
		|| !context.Parsed.TryGetValue<int>("limit", out var limit) || limit != 0
		|| !context.Parsed.TryGetValue<string>("text", out var text)) { return ReplInvocationResult.Continue(); }
	return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("query {text}",
		new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["text"] = text, ["limit"] = policy.MaximumLimit },
		context.Parsed.GlobalValues.Where(pair => pair.Value.State == InputValueState.Available)
			.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase)));
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
		7. At startup, --maximum-limit 2 configures SearchPolicy before DI; /find "hello world" 3 is then refused.
		8. At startup, --refresh-on-start status refreshes once before dispatch; --refresh-on-start alone finishes without a command.
		9. Submit /find "hello world" 0: a parsed hook selects the injected policy maximum, then normal validation and search run.
		""");

return await app.RunAsync(prepared);

internal sealed record SearchPolicy(int MaximumLimit);

internal sealed class SampleStartupOptions
{
	public int MaximumLimit { get; set; } = 5;
	public bool RefreshOnStart { get; set; }
}

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
