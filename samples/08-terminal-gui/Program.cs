using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Repl;
using Repl.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// ──────────────────────────────────────────────────────────────
// Build the REPL app — same commands work in console, Spectre,
// WebSocket, Telnet… and now Terminal.Gui.
// ──────────────────────────────────────────────────────────────
var app = ReplApp.Create(services =>
	{
		services.AddSingleton<IContactStore, InMemoryContactStore>();
		services.AddSpectreConsole(); // Rich Spectre output (tables, progress, etc.)
		services.AddTerminalGui();    // Terminal.Gui modal dialogs for prompts
	})
	.WithDescription("Terminal.Gui TUI host with Spectre output")
	.UseDefaultInteractive()
	.UseCliProfile()
	.UseSpectreConsole();

// ── Commands ─────────────────────────────────────────────────

app.Map("list",
	[Description("List all contacts")]
	(IContactStore store) => store.All());

app.Map("detail {name}",
	[Description("Show contact details")]
	(string name, IContactStore store) =>
	{
		var contact = store.Get(name);
		return contact is null
			? Results.NotFound($"Contact '{name}' not found.")
			: Results.Success($"Found {contact.Name}.", contact);
	});

app.Map("add",
	[Description("Add a contact (interactive)")]
	async (IReplInteractionChannel channel, IContactStore store, CancellationToken ct) =>
	{
		var name = await channel.AskTextAsync("name", "Contact name?");
		while (string.IsNullOrWhiteSpace(name))
		{
			name = await channel.AskTextAsync("name", "Contact name?");
		}

		var email = await channel.AskTextAsync("email", "Email address?");
		var phone = await channel.AskTextAsync("phone", "Phone number?");

		string[] departments = ["Engineering", "Sales", "Marketing", "Support"];
		var deptIndex = await channel.AskChoiceAsync("department", "Department?", departments);

		store.Add(new Contact(name, email, phone, departments[deptIndex]));
		return Results.Success($"Contact '{name}' added.");
	});

app.Map("configure",
	[Description("Configure features (multi-selection)")]
	async (IReplInteractionChannel channel, CancellationToken ct) =>
	{
		var selected = await channel.AskMultiChoiceAsync(
			"features",
			"Enable features:",
			["Authentication", "Logging", "Caching", "Metrics", "Rate Limiting"],
			defaultIndices: [0, 1]);

		return Results.Success($"Enabled {selected.Count} feature(s).");
	});

app.Map("login",
	[Description("Simulate login (secret input)")]
	async (IReplInteractionChannel channel, CancellationToken ct) =>
	{
		var username = await channel.AskTextAsync("username", "Username?");
		var password = await channel.AskSecretAsync("password", "Password?");
		return Results.Success($"Logged in as '{username}' (password length: {password.Length}).");
	});

app.Map("confirm",
	[Description("Confirm an action")]
	async (IReplInteractionChannel channel, CancellationToken ct) =>
	{
		var result = await channel.AskConfirmationAsync("proceed", "Proceed with operation?", defaultValue: true);
		return result ? Results.Success("Confirmed.") : Results.Cancelled("Declined.");
	});

app.Map("figlet {text}",
	[Description("Render large colored ASCII art")]
	(string text, IAnsiConsole console) =>
	{
		console.Write(new FigletText(text).Color(Color.Green));
		return Results.Success($"Rendered '{text}'.");
	});

// ──────────────────────────────────────────────────────────────
// Terminal.Gui layout — the developer owns the shell
// ──────────────────────────────────────────────────────────────
#pragma warning disable CS0618 // Static Application API — Terminal.Gui v2 develop

Application.Init();

var outputView = new ReplOutputView
{
	X = 0,
	Y = 0,
	Width = Dim.Fill(),
	Height = Dim.Fill(1),
};

var inputView = new ReplInputView
{
	X = 0,
	Y = Pos.AnchorEnd(1),
	Width = Dim.Fill(),
};

var window = new Window
{
	Title = "Repl.TerminalGui Sample — Contacts",
};

window.Add(outputView, inputView);

using var session = new ReplSession(app, outputView, inputView);
var exitCode = await session.RunAsync(window);

Application.Shutdown();

#pragma warning restore CS0618

return exitCode;
