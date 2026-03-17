using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Repl;

var app = ReplApp.Create(services =>
	{
		services.AddSingleton<IContactStore, InMemoryContactStore>();
		services.AddSpectreConsole();
	})
	.WithDescription("Spectre.Console integration: rich renderables, interactive prompts, data visualization.")
	.WithBanner((IAnsiConsole console) =>
	{
		console.Write(new FigletText("Spectre").Color(Color.Blue));
		console.MarkupLine("  [grey]Commands:[/] tour, list, detail, chart, tree, json, path, calendar,");
		console.MarkupLine("           figlet, status, progress, add, configure, login");
	})
	.UseDefaultInteractive()
	.UseCliProfile()
	.UseSpectreConsole();

// ──────────────────────────────────────────────────────────────
// tour — Guided walkthrough (the star command)
// ──────────────────────────────────────────────────────────────
app.Map("tour",
	[Description("Guided walkthrough of Spectre features")]
	async (IAnsiConsole console, IReplInteractionChannel channel, IContactStore store, CancellationToken ct) =>
	{
		// 1. FigletText welcome banner
		console.Write(new FigletText("Welcome!").Color(Color.Blue));

		// 2. TextPrompt — ask user's name
		var name = await channel.AskTextAsync("name", "What is your name?");
		if (string.IsNullOrWhiteSpace(name))
		{
			name = "Guest";
		}

		// 3. Panel — greeting
		console.Write(new Panel(new Markup($"Hello, [bold yellow]{Markup.Escape(name)}[/]!"))
			.Header("Greeting")
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Green));
		console.WriteLine();

		// 4. SelectionPrompt — choose a data category
		string[] categories = ["All contacts", "By domain", "By department"];
		var categoryIndex = await channel.AskChoiceAsync("category", "Choose a data category:", categories);
		var category = categories[categoryIndex];

		// 5. Table — display data for chosen category
		var contacts = store.All();
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue)
			.AddColumn("Name").AddColumn("Email").AddColumn("Department");

		IEnumerable<Contact> displayContacts = categoryIndex switch
		{
			1 => store.GroupByDomain().First().Value,
			2 => contacts.GroupBy(c => c.Department).First(),
			_ => contacts,
		};

		foreach (var c in displayContacts)
		{
			table.AddRow(Markup.Escape(c.Name), Markup.Escape(c.Email), Markup.Escape(c.Department));
		}

		console.Write(table);
		console.WriteLine();

		// 6. BarChart — stats
		var domainGroups = store.GroupByDomain();
		var barChart = new BarChart().Label("[bold]Contacts by Domain[/]").CenterLabel();
		var colors = new[] { Color.Blue, Color.Green, Color.Red, Color.Yellow, Color.Purple };
		var i = 0;
		foreach (var (domain, domainContacts) in domainGroups)
		{
			barChart.AddItem(domain, domainContacts.Count, colors[i++ % colors.Length]);
		}

		console.Write(barChart);
		console.WriteLine();

		// 7. Tree — hierarchical breakdown
		var tree = new Tree("Contacts");
		foreach (var (domain, domainContacts) in domainGroups)
		{
			var node = tree.AddNode($"[yellow]{Markup.Escape(domain)}[/]");
			foreach (var c in domainContacts)
			{
				node.AddNode(Markup.Escape(c.Name));
			}
		}

		console.Write(tree);
		console.WriteLine();

		// 8. ConfirmationPrompt — continue to summary?
		var continueToSummary = await channel.AskConfirmationAsync(
			"continue", "Continue to summary?", defaultValue: true);
		if (!continueToSummary)
		{
			return Results.Cancelled("Tour stopped by user.");
		}

		// 9. Calendar — current month
		var today = DateTime.Today;
		var calendar = new Calendar(today.Year, today.Month)
			.AddCalendarEvent(today)
			.HighlightStyle(new Style(Color.Blue, decoration: Decoration.Bold));
		console.Write(new Panel(calendar).Header("This Month").Border(BoxBorder.Rounded));
		console.WriteLine();

		// 10. Rule + summary Panel
		console.Write(new Rule("[blue]Summary[/]").RuleStyle(new Style(Color.Grey)));
		console.Write(new Panel(new Markup(
				$"[bold]Tour complete![/]\n" +
				$"  Name: [yellow]{Markup.Escape(name)}[/]\n" +
				$"  Category: [green]{Markup.Escape(category)}[/]\n" +
				$"  Contacts: [blue]{contacts.Count}[/]\n" +
				$"  Domains: [blue]{domainGroups.Count}[/]"))
			.Header("Results")
			.Border(BoxBorder.Double)
			.BorderColor(Color.Gold1));

		return Results.Success("Tour complete!");
	});

// ──────────────────────────────────────────────────────────────
// list — Returns collection, auto-rendered as Spectre table
// ──────────────────────────────────────────────────────────────
app.Map("list",
	[Description("List all contacts (auto-rendered table)")]
	(IContactStore store) => store.All());

// ──────────────────────────────────────────────────────────────
// detail — Panel + Grid
// ──────────────────────────────────────────────────────────────
app.Map("detail {name}",
	[Description("Show contact details in a panel")]
	(string name, IContactStore store, IAnsiConsole console) =>
	{
		var contact = store.Get(name);
		if (contact is null)
		{
			return Results.NotFound($"Contact '{name}' not found.");
		}

		var grid = new Grid();
		grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
		grid.AddColumn(new GridColumn());
		grid.AddRow(new Markup("[bold]Name[/]"), new Markup(Markup.Escape(contact.Name)));
		grid.AddRow(new Markup("[bold]Email[/]"), new Markup(Markup.Escape(contact.Email)));
		grid.AddRow(new Markup("[bold]Phone[/]"), new Markup(Markup.Escape(contact.Phone)));
		grid.AddRow(new Markup("[bold]Dept[/]"), new Markup(Markup.Escape(contact.Department)));

		console.Write(new Panel(grid)
			.Header(Markup.Escape(contact.Name))
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Blue));

		return Results.Success($"Details for {contact.Name}.");
	});

// ──────────────────────────────────────────────────────────────
// chart — BarChart + BreakdownChart
// ──────────────────────────────────────────────────────────────
app.Map("chart",
	[Description("Visualize contacts by domain")]
	(IContactStore store, IAnsiConsole console) =>
	{
		var groups = store.GroupByDomain();
		var colors = new[] { Color.Blue, Color.Green, Color.Red, Color.Yellow, Color.Purple, Color.Aqua };

		var barChart = new BarChart()
			.Label("[bold]Contacts per Domain[/]")
			.CenterLabel();
		var i = 0;
		foreach (var (domain, contacts) in groups)
		{
			barChart.AddItem(domain, contacts.Count, colors[i++ % colors.Length]);
		}

		console.Write(barChart);
		console.WriteLine();

		var breakdown = new BreakdownChart();
		i = 0;
		foreach (var (domain, contacts) in groups)
		{
			breakdown.AddItem(domain, contacts.Count, colors[i++ % colors.Length]);
		}

		console.Write(new Panel(breakdown)
			.Header("Email Provider Breakdown")
			.Border(BoxBorder.Rounded));

		return Results.Success($"{groups.Count} domains visualized.");
	});

// ──────────────────────────────────────────────────────────────
// tree — Tree view
// ──────────────────────────────────────────────────────────────
app.Map("tree",
	[Description("Show contacts as a tree by domain")]
	(IContactStore store, IAnsiConsole console) =>
	{
		var groups = store.GroupByDomain();
		var tree = new Tree("[bold]Contacts[/]");

		foreach (var (domain, contacts) in groups)
		{
			var node = tree.AddNode($"[yellow]@{Markup.Escape(domain)}[/] [grey]({contacts.Count})[/]");
			foreach (var c in contacts)
			{
				node.AddNode($"{Markup.Escape(c.Name)} [grey]- {Markup.Escape(c.Department)}[/]");
			}
		}

		console.Write(tree);
		return Results.Success($"{store.All().Count} contacts in {groups.Count} domains.");
	});

// ──────────────────────────────────────────────────────────────
// json — JsonText
// ──────────────────────────────────────────────────────────────
app.Map("json {name}",
	[Description("Show contact as syntax-highlighted JSON")]
	(string name, IContactStore store, IAnsiConsole console) =>
	{
		var contact = store.Get(name);
		if (contact is null)
		{
			return Results.NotFound($"Contact '{name}' not found.");
		}

		var json = JsonSerializer.Serialize(contact, JsonOptions.Indented);
		console.Write(new Panel(new JsonText(json))
			.Header("JSON")
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Yellow));

		return Results.Success($"JSON for {contact.Name}.");
	});

// ──────────────────────────────────────────────────────────────
// path — TextPath + Columns
// ──────────────────────────────────────────────────────────────
app.Map("path",
	[Description("Show file paths with TextPath in Columns layout")]
	(IAnsiConsole console) =>
	{
		string[] paths =
		[
			"C:/Projects/ContactManager/src/Models/Contact.cs",
			"C:/Projects/ContactManager/src/Services/ContactStore.cs",
			"C:/Projects/ContactManager/src/Controllers/ContactController.cs",
			"C:/Projects/ContactManager/tests/ContactStoreTests.cs",
			"C:/Projects/ContactManager/docs/README.md",
			"C:/Projects/ContactManager/config/appsettings.json",
		];

		var textPaths = paths.Select(p => (IRenderable)new TextPath(p)
			.StemColor(Color.Yellow)
			.LeafColor(Color.Green)
			.SeparatorColor(Color.Grey)
			.RootColor(Color.Blue)).ToArray();

		console.Write(new Columns(textPaths));
		return Results.Success($"{paths.Length} paths displayed.");
	});

// ──────────────────────────────────────────────────────────────
// calendar — Calendar
// ──────────────────────────────────────────────────────────────
app.Map("calendar",
	[Description("Show a calendar with highlighted dates")]
	(IAnsiConsole console) =>
	{
		var today = DateTime.Today;
		var calendar = new Calendar(today.Year, today.Month)
			.AddCalendarEvent(today)
			.AddCalendarEvent(today.Year, today.Month, 1)
			.AddCalendarEvent(today.Year, today.Month, 15)
			.HighlightStyle(new Style(Color.Blue, decoration: Decoration.Bold))
			.HeaderStyle(new Style(Color.Yellow));

		console.Write(new Panel(calendar)
			.Header($"{today:MMMM yyyy}")
			.Border(BoxBorder.Rounded)
			.BorderColor(Color.Blue));

		return Results.Success("Calendar displayed.");
	});

// ──────────────────────────────────────────────────────────────
// figlet — FigletText
// ──────────────────────────────────────────────────────────────
app.Map("figlet {text}",
	[Description("Render large ASCII art text")]
	(string text, IAnsiConsole console) =>
	{
		console.Write(new FigletText(text).Color(Color.Green));
		return Results.Success($"Rendered '{text}' as FigletText.");
	});

// ──────────────────────────────────────────────────────────────
// status — Status spinner
// ──────────────────────────────────────────────────────────────
app.Map("status",
	[Description("Show a spinner progressing through stages")]
	async (IAnsiConsole console, CancellationToken ct) =>
	{
		await console.Status().StartAsync("Initializing...", async ctx =>
		{
			ctx.Spinner = Spinner.Known.Dots;
			ctx.SpinnerStyle = new Style(Color.Blue);
			await Task.Delay(1000, ct);

			ctx.Status = "Loading contacts...";
			ctx.SpinnerStyle = new Style(Color.Green);
			await Task.Delay(1000, ct);

			ctx.Status = "Building index...";
			ctx.SpinnerStyle = new Style(Color.Yellow);
			await Task.Delay(1000, ct);

			ctx.Status = "Finalizing...";
			ctx.SpinnerStyle = new Style(Color.Purple);
			await Task.Delay(1000, ct);
		});

		return Results.Success("All stages complete.");
	});

// ──────────────────────────────────────────────────────────────
// progress — Progress bars
// ──────────────────────────────────────────────────────────────
app.Map("progress",
	[Description("Show multi-task progress bars")]
	async (IAnsiConsole console, CancellationToken ct) =>
	{
		await console.Progress()
			.Columns(
				new TaskDescriptionColumn(),
				new ProgressBarColumn(),
				new PercentageColumn(),
				new SpinnerColumn())
			.StartAsync(async ctx =>
			{
				var task1 = ctx.AddTask("Downloading contacts");
				var task2 = ctx.AddTask("Processing records");
				var task3 = ctx.AddTask("Generating reports");

				while (!ctx.IsFinished)
				{
					await Task.Delay(50, ct);
					task1.Increment(1.5);
					task2.Increment(0.9);
					task3.Increment(0.5);
				}
			});

		return Results.Success("All tasks complete.");
	});

// ──────────────────────────────────────────────────────────────
// add — Interactive prompts (transparent Spectre upgrade)
// ──────────────────────────────────────────────────────────────
app.Map("add",
	[Description("Add a contact (interactive prompts via Spectre)")]
	async (IReplInteractionChannel channel, IContactStore store, CancellationToken ct) =>
	{
		var name = await channel.AskTextAsync("name", "Contact name?");
		while (string.IsNullOrWhiteSpace(name))
		{
			name = await channel.AskTextAsync("name", "Contact name?");
		}

		var email = await channel.AskTextAsync("email", "Email address?");
		var phone = await channel.AskTextAsync("phone", "Phone number?");

		string[] departments = ["Engineering", "Sales", "Marketing", "Support", "Management"];
		var deptIndex = await channel.AskChoiceAsync("department", "Department?", departments);

		store.Add(new Contact(name, email, phone, departments[deptIndex]));
		return Results.Success($"Contact '{name}' added.");
	});

// ──────────────────────────────────────────────────────────────
// configure — MultiSelectionPrompt
// ──────────────────────────────────────────────────────────────
app.Map("configure",
	[Description("Configure features (multi-selection)")]
	async (IReplInteractionChannel channel, CancellationToken ct) =>
	{
		var selected = await channel.AskMultiChoiceAsync(
			"features",
			"Enable features:",
			["Authentication", "Logging", "Caching", "Metrics", "Rate Limiting", "Compression"],
			defaultIndices: [0, 1]);

		return Results.Success($"Enabled {selected.Count} feature(s).");
	});

// ──────────────────────────────────────────────────────────────
// login — Secret input
// ──────────────────────────────────────────────────────────────
app.Map("login",
	[Description("Simulate login (secret input via Spectre)")]
	async (IReplInteractionChannel channel, CancellationToken ct) =>
	{
		var username = await channel.AskTextAsync("username", "Username?");
		var password = await channel.AskSecretAsync("password", "Password?");
		return Results.Success($"Logged in as '{username}' (password length: {password.Length}).");
	});

return app.Run(args);

file static class JsonOptions
{
	internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
