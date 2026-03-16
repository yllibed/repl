using System.Text;

namespace Repl.TerminalGui;

/// <summary>
/// A <see cref="TextWriter"/> that feeds text into a <see cref="ReplOutputView"/>,
/// marshalling writes to the Terminal.Gui UI thread.
/// </summary>
internal sealed class TerminalGuiTextWriter : TextWriter
{
	private readonly ReplOutputView _outputView;

	public TerminalGuiTextWriter(ReplOutputView outputView)
	{
		ArgumentNullException.ThrowIfNull(outputView);
		_outputView = outputView;
	}

	/// <inheritdoc />
	public override Encoding Encoding => Encoding.UTF8;

	/// <inheritdoc />
	public override void Write(char value) => WriteCore(value.ToString());

	/// <inheritdoc />
	public override void Write(string? value)
	{
		if (!string.IsNullOrEmpty(value))
		{
			WriteCore(value);
		}
	}

	/// <inheritdoc />
	public override void WriteLine(string? value) => WriteCore((value ?? string.Empty) + NewLine);

	/// <inheritdoc />
	public override Task WriteAsync(char value)
	{
		Write(value);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public override Task WriteAsync(string? value)
	{
		Write(value);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public override Task WriteLineAsync(string? value)
	{
		WriteLine(value);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	private void WriteCore(string text) => _outputView.AppendText(text);
}
