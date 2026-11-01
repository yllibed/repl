namespace Repl.Remote;

/// <summary>Attaches the current console to a remote terminal. Ctrl+D detaches locally.</summary>
public static class RemoteConsole
{
	/// <summary>Runs an interactive console, restoring local console state on every exit path.</summary>
	public static Task<int> RunAsync(RemoteReplClient client, CancellationToken cancellationToken = default) =>
		RunAsync(client, new SystemRemoteConsole(), cancellationToken);

	internal static async Task<int> RunAsync(RemoteReplClient client, IRemoteConsole console, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(client);
		using var state = console.Enter();
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var size = console.Size;
		await client.DiscoverAsync(lifetime.Token).ConfigureAwait(false);
		var terminal = await client.OpenTerminalAsync(new() { Columns = size.Columns, Rows = size.Rows }, lifetime.Token).ConfigureAwait(false);
		await using var terminalOwner = terminal.ConfigureAwait(false);
		var output = CopyOutputAsync(terminal, console, lifetime.Token);
		var input = CopyInputAsync(terminal, console, size, lifetime.Token);
		try
		{
			var finished = await Task.WhenAny(terminal.Completion, input, output).ConfigureAwait(false);
			if (ReferenceEquals(finished, input))
			{
				await input.ConfigureAwait(false);
				await terminal.DisposeAsync().ConfigureAwait(false);
			}
			else if (ReferenceEquals(finished, output)) { await output.ConfigureAwait(false); }
#pragma warning disable VSTHRD003 // A remote terminal cannot depend on the local UI thread.
			var exitCode = await terminal.Completion.WaitAsync(lifetime.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			await output.ConfigureAwait(false);
			return exitCode;
		}
		finally
		{
			await lifetime.CancelAsync().ConfigureAwait(false);
			try { await Task.WhenAll(input, output).ConfigureAwait(false); }
			catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		}
	}

	private static async Task CopyOutputAsync(RemoteTerminal terminal, IRemoteConsole console, CancellationToken cancellationToken)
	{
		await foreach (var text in terminal.Output.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			await console.WriteAsync(text, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task CopyInputAsync(RemoteTerminal terminal, IRemoteConsole console, (int Columns, int Rows) size,
		CancellationToken cancellationToken)
	{
		char? surrogate = null;
		while (true)
		{
			var key = await console.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
			var currentSize = console.Size;
			if (currentSize != size)
			{
				size = currentSize;
				await terminal.ResizeAsync(size.Columns, size.Rows, cancellationToken).ConfigureAwait(false);
			}
			if (key is not { } value) { continue; }
			var control = value.Modifiers.HasFlag(ConsoleModifiers.Control);
			if (value.KeyChar == '\u0004' || (control && value.Key == ConsoleKey.D)) { return; }
			if (value.KeyChar == '\u0003' || (control && value.Key == ConsoleKey.C))
			{
				await terminal.InterruptAsync(cancellationToken).ConfigureAwait(false);
				continue;
			}
			if (char.IsHighSurrogate(value.KeyChar)) { surrogate = value.KeyChar; continue; }
			var text = EncodeKey(value);
			if (surrogate is { } high) { text = high + text; surrogate = null; }
			if (text.Length > 0) { await terminal.InputAsync(text, cancellationToken).ConfigureAwait(false); }
		}
	}

	private static string EncodeKey(ConsoleKeyInfo key)
	{
		var text = key.Key switch
		{
			ConsoleKey.UpArrow => "\u001b[A",
			ConsoleKey.DownArrow => "\u001b[B",
			ConsoleKey.RightArrow => "\u001b[C",
			ConsoleKey.LeftArrow => "\u001b[D",
			ConsoleKey.Home => "\u001b[H",
			ConsoleKey.End => "\u001b[F",
			ConsoleKey.Delete => "\u001b[3~",
			ConsoleKey.Insert => "\u001b[2~",
			ConsoleKey.PageUp => "\u001b[5~",
			ConsoleKey.PageDown => "\u001b[6~",
			ConsoleKey.Backspace => "\u007f",
			ConsoleKey.Enter => "\r",
			ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift) => "\u001b[Z",
			_ => key.KeyChar == '\0' ? string.Empty : key.KeyChar.ToString(),
		};
		return key.Modifiers.HasFlag(ConsoleModifiers.Alt) ? "\u001b" + text : text;
	}
}
