namespace Samples.Testing;

internal static class SampleReplApp
{
	public static ReplApp Create() => Create(new SharedState());

	public static ReplApp Create(SharedState sharedState)
	{
		ArgumentNullException.ThrowIfNull(sharedState);
		var state = sharedState;
		var widgets = new[]
		{
			new Widget(1, "Alpha"),
			new Widget(2, "Beta"),
		};

		var app = ReplApp.Create().UseDefaultInteractive();
		app.Map("ping", () => "pong");
		app.Map("counter inc", () =>
		{
			state.Counter++;
			return state.Counter;
		});
		app.Map("counter get", () => state.Counter);
		app.Map("settings set {key} {value}", (string key, string value) =>
		{
			state.Settings[key] = value;
			return $"{key}={value}";
		});
		app.Map("settings show {key}", (string key) =>
			(object)(state.Settings.TryGetValue(key, out var value)
				? value
				: Results.NotFound($"Setting '{key}' not found.")));
		app.Map("widget show {id:int}", (int id) =>
		{
			var widget = widgets.SingleOrDefault(item => item.Id == id);
			return (object)(widget is null
				? Results.NotFound($"Widget '{id}' not found.")
				: widget);
		});
		app.Map("import", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Import started", ct).ConfigureAwait(false);
			return "done";
		});
		return app;
	}

	internal sealed record Widget(int Id, string Name);

	internal sealed class SharedState
	{
		public int Counter { get; set; }

		public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase)
		{
			["maintenance"] = "off",
		};
	}
}
