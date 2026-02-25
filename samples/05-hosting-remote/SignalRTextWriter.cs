using System.Text;

using Microsoft.AspNetCore.SignalR;

namespace HostingRemoteSample;

/// <summary>
/// A <see cref="TextWriter"/> that sends text to a SignalR client via <see cref="ISingleClientProxy"/>.
/// </summary>
internal sealed class SignalRTextWriter(ISingleClientProxy caller) : TextWriter
{
	public override Encoding Encoding => Encoding.UTF8;

	public override Task WriteAsync(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return Task.CompletedTask;
		}

		return caller.SendAsync("Output", value);
	}

	public override Task WriteLineAsync(string? value) =>
		WriteAsync((value ?? string.Empty) + Environment.NewLine);
}
