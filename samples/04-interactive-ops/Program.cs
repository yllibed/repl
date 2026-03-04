using System.ComponentModel;
using Repl;
using Microsoft.Extensions.DependencyInjection;

// Sample goal: full IReplInteractionChannel coverage
// - AskTextAsync with retry-on-invalid (add)
// - AskChoiceAsync with default + prefix matching (import)
// - AskConfirmationAsync with safe default (clear)
// - AskSecretAsync for masked input (login)
// - AskMultiChoiceAsync for multi-selection (configure)
// - AskEnumAsync for enum-based choice (theme)
// - AskNumberAsync for typed numeric input (set-limit)
// - AskValidatedTextAsync for validated text (set-email)
// - PressAnyKeyAsync for interactive pause (demo)
// - WriteStatusAsync for inline feedback (import, add)
// - IProgress<ReplProgressEvent> structured progress (import)
// - IProgress<double> simple progress (sync)
// - optional route parameters ({name?}) with prompt-for-missing-values
// - --answer:* prefill for non-interactive CLI automation
// - Esc-key cancellation, prompt timeout, long-running watch command
var app = ReplApp.Create(services =>
	{
		services.AddSingleton<IContactStore, InMemoryContactStore>();

		// Seed history with example commands so the user can press Up right away.
		// In a real app, implement IHistoryProvider to persist entries to disk or a
		// database so history survives across sessions.
		services.AddSingleton<IHistoryProvider>(new InMemoryHistoryProvider([
			"contact add",
			"contact import 123",
			"contact list",
			"contact sync",
			"contact clear",
			"contact watch",
		]));
	})
	.WithDescription("Interactive ops sample: guided import with confirmation, progress, and structured output.")
	.WithBanner("""
		  Try: contact import 123    (guided flow + countdown timeout)
		  Try: contact add           (prompts for missing fields)
		  Try: contact clear         (confirmation prompt)
		  Try: contact sync          (simple progress)
		  Try: contact watch         (Ctrl+C to stop)
		  Try: contact login         (secret/password input)
		  Try: contact configure     (multi-choice selection)
		  Try: contact theme         (enum-based choice)
		  Try: contact set-limit     (typed numeric input)
		  Try: contact set-email     (validated text input)
		  Try: contact demo          (press any key pause)
		""")
	.UseDefaultInteractive()
	.UseCliProfile();

app.Context(
	"contact",
	[Description("Manage contacts")]
	(IReplMap contact) =>
	{
		contact.WithBanner((TextWriter w, IContactStore store) =>
		{
			w.WriteLine($"  {store.All().Count} contact(s). Commands: list, add, import, clear, sync, watch");
		});

		contact.Map("list",
			[Description("List all contacts")]
			(IContactStore store) => store.All());

		// Optional route params — omitted values arrive as null, handler prompts interactively.
		// Retry loops let the user correct mistakes without restarting the command.
		contact.Map(
			"add {name?} {email?:email}",
			[Description("Add a contact (prompts for missing fields)")]
			async (
				string? name,
				string? email,
				IContactStore store,
				IReplInteractionChannel channel,
				CancellationToken cancellationToken) =>
			{
				// Ambient per-command CT — no need to pass cancellationToken to Ask methods.
				while (string.IsNullOrWhiteSpace(name))
				{
					name = await channel.AskTextAsync("name", "Contact name?");
				}

				while (string.IsNullOrWhiteSpace(email) || !System.Net.Mail.MailAddress.TryCreate(email, out _))
				{
					if (!string.IsNullOrWhiteSpace(email))
					{
						await channel.WriteStatusAsync($"'{email}' is not a valid email address.", cancellationToken);
					}

					email = await channel.AskTextAsync("email", "Email address?");
				}

				store.Add(new Contact(name, email));
				return Results.Success($"Contact '{name}' added.", new Contact(name, email));
			});

		// Full interactive flow: status, choice prompt, structured progress.
		// File parsing is simulated — no real I/O, contacts are generated from the filename.
		contact.Map(
			"import {file}",
			[Description("Import contacts from a file (guided)")]
			async (
				string file,
				IContactStore store,
				IReplInteractionChannel channel,
				IProgress<ReplProgressEvent> progress,
				CancellationToken cancellationToken) =>
			{
				await channel.WriteStatusAsync($"Parsing '{file}'...", cancellationToken);
				var batch = await store.ParseFileAsync(file, cancellationToken);
				var duplicates = store.CountDuplicates(batch);

				// N-way choice prompt with default — prefillable via --answer:duplicates=skip
				// Ambient CT + 10s timeout — auto-selects default after countdown.
				const int Skip = 0, Cancel = 2;
				var skipDuplicates = false;
				if (duplicates > 0)
				{
					await channel.WriteStatusAsync($"Detected {duplicates} duplicate(s).", cancellationToken);

					var strategy = await channel.AskChoiceAsync(
						"duplicates",
						"How to handle duplicates?",
						["Skip", "Overwrite", "Cancel"],
						defaultIndex: Skip,
						new AskOptions(Timeout: TimeSpan.FromSeconds(10)));

					if (strategy == Cancel)
					{
						return Results.Cancelled("Import cancelled by user.");
					}

					skipDuplicates = strategy == Skip;
				}

				var imported = 0;
				var overwritten = 0;
				var skipped = 0;

				for (var i = 0; i < batch.Count; i++)
				{
					progress.Report(new ReplProgressEvent("Importing", Current: i + 1, Total: batch.Count));

					var isDuplicate = store.IsDuplicate(batch[i]);
					if (isDuplicate && skipDuplicates)
					{
						skipped++;
						continue;
					}

					await store.ImportOneAsync(batch[i], cancellationToken);
					if (isDuplicate)
					{
						overwritten++;
					}

					imported++;
				}

				return Results.Success(
					$"{imported} imported, {overwritten} overwritten, {skipped} skipped.",
					new ImportSummary(imported, overwritten, skipped));
			});

		// Confirmation prompt — defaults to "no" so accidental Enter doesn't wipe data.
		contact.Map(
			"clear",
			[Description("Remove all contacts")]
			async (
				IContactStore store,
				IReplInteractionChannel channel,
				CancellationToken cancellationToken) =>
			{
				var count = store.All().Count;
				if (count == 0)
				{
					return Results.Success("No contacts to remove.");
				}

				var confirmed = await channel.AskConfirmationAsync(
					"confirm",
					$"Delete all {count} contact(s)?");

				if (!confirmed)
				{
					return Results.Cancelled("Clear cancelled.");
				}

				store.Clear();
				return Results.Success($"{count} contact(s) removed.");
			});

		// Simple percentage-based progress with IProgress<double>.
		contact.Map(
			"sync",
			[Description("Simulate a sync operation (simple progress)")]
			async (IProgress<double> progress, CancellationToken cancellationToken) =>
			{
				const int steps = 15;
				for (var i = 1; i <= steps; i++)
				{
					await Task.Delay(80, cancellationToken);
					progress.Report(i * 100.0/steps);
				}

				return Results.Success("Sync completed.");
			});

		// Long-running command — runs until Ctrl+C via cooperative cancellation.
		// First Ctrl+C cancels the per-command token; second Ctrl+C exits the session.
		contact.Map(
			"watch",
			[Description("Watch for changes (Ctrl+C to stop)")]
			async (
				IContactStore store,
				IReplInteractionChannel channel,
				CancellationToken ct) =>
			{
				while (!ct.IsCancellationRequested)
				{
					await channel.WriteStatusAsync(
						$"Watching... {store.All().Count} contacts. (Ctrl+C to stop)", ct);
					await Task.Delay(2000, ct);
				}

				return Results.Cancelled("Watch stopped.");
			});

		// Secret input — password/token prompt with masked echo.
		// Characters are shown as '*' by default, never echoed in clear.
		// Prefillable via --answer:password=value for CI.
		contact.Map(
			"login",
			[Description("Simulate login (secret input)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				var password = await channel.AskSecretAsync("password", "Password?");
				return Results.Success($"Logged in (password length: {password.Length}).");
			});

		// Multi-choice — select one or more features from a list.
		// Enter numbers separated by commas: 1,3
		contact.Map(
			"configure",
			[Description("Configure features (multi-choice)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				var selected = await channel.AskMultiChoiceAsync(
					"features",
					"Enable features:",
					["Authentication", "Logging", "Caching", "Metrics"],
					defaultIndices: [0, 1]);
				return Results.Success($"Enabled feature indices: {string.Join(", ", selected)}.");
			});

		// Enum choice — pick from enum values, humanized automatically.
		contact.Map(
			"theme",
			[Description("Choose a theme (enum choice)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				var theme = await channel.AskEnumAsync<AppTheme>("theme", "Choose a theme:", AppTheme.System);
				return Results.Success($"Theme set to {theme}.");
			});

		// Number input — typed numeric prompt with min/max bounds.
		contact.Map(
			"set-limit",
			[Description("Set import limit (numeric input)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				var limit = await channel.AskNumberAsync<int>(
					"limit",
					"Max contacts to import?",
					defaultValue: 100,
					new AskNumberOptions<int>(Min: 1, Max: 10000));
				return Results.Success($"Import limit set to {limit}.");
			});

		// Validated text input — re-prompts until validation passes.
		contact.Map(
			"set-email",
			[Description("Set notification email (validated)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				var email = await channel.AskValidatedTextAsync(
					"email",
					"Notification email?",
					input => System.Net.Mail.MailAddress.TryCreate(input, out _)
						? null
						: $"'{input}' is not a valid email address.");
				return Results.Success($"Notification email set to {email}.");
			});

		// Press any key — simple interactive pause.
		contact.Map(
			"demo",
			[Description("Interactive demo (press any key)")]
			async (IReplInteractionChannel channel, CancellationToken cancellationToken) =>
			{
				await channel.WriteStatusAsync("Step 1: Preparing data...", cancellationToken);
				await channel.PressAnyKeyAsync("Press any key to continue to step 2...", cancellationToken);
				await channel.WriteStatusAsync("Step 2: Processing complete.", cancellationToken);
				return Results.Success("Demo finished.");
			});
	});

return app.Run(args);
