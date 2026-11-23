namespace Repl.Hosting.TestHost;

/// <summary>Holds back every write of a shutdown failure for <paramref name="delay"/>, and nothing else.</summary>
internal sealed class FatalLineDelayingWriter(TextWriter inner, TimeSpan delay) : TextWriter
{
	public override System.Text.Encoding Encoding => inner.Encoding;

	public override void Write(char value) => inner.Write(value);

	public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

	public override void Write(ReadOnlySpan<char> buffer) => Write(new string(buffer));

	public override void Write(string? value)
	{
		if (value?.Contains("Administration shutdown failed", StringComparison.Ordinal) == true)
		{
#pragma warning disable MA0045 // Deliberately slow output, isolated in a child process.
			Thread.Sleep(delay);
#pragma warning restore MA0045
		}

		inner.Write(value);
	}

	public override void Flush() => inner.Flush();
}
