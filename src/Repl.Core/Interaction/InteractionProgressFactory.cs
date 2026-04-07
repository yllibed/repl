namespace Repl;

internal static class InteractionProgressFactory
{
	public static bool TryCreate(
		Type parameterType,
		InvocationBindingContext context,
		out object? progress)
	{
		ArgumentNullException.ThrowIfNull(parameterType);
		ArgumentNullException.ThrowIfNull(context);

		var channel = context.ServiceProvider.GetService(typeof(IReplInteractionChannel)) as IReplInteractionChannel;
		if (channel is null)
		{
			progress = null;
			return false;
		}

		if (parameterType == typeof(IProgress<double>))
		{
			progress = new PercentageProgress(channel, context.InteractionOptions, context.CancellationToken);
			return true;
		}

		if (parameterType == typeof(IProgress<ReplProgressEvent>))
		{
			progress = new StructuredProgress(channel, context.CancellationToken);
			return true;
		}

		progress = null;
		return false;
	}

	private sealed class PercentageProgress(
		IReplInteractionChannel channel,
		InteractionOptions options,
		CancellationToken cancellationToken) : IProgress<double>
	{
		public void Report(double value)
		{
			var label = string.IsNullOrWhiteSpace(options.DefaultProgressLabel)
				? "Progress"
				: options.DefaultProgressLabel;
#pragma warning disable VSTHRD002 // IProgress<T>.Report is sync by contract; we bridge to async channel intentionally.
			channel.WriteProgressAsync(label, value, cancellationToken).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
		}
	}

	private sealed class StructuredProgress(
		IReplInteractionChannel channel,
		CancellationToken cancellationToken) : IProgress<ReplProgressEvent>
	{
		public void Report(ReplProgressEvent value)
		{
			var percent = value.ResolvePercent();
#pragma warning disable VSTHRD002 // IProgress<T>.Report is sync by contract; we bridge to async channel intentionally.
			channel.WriteProgressAsync(value.Label, percent, cancellationToken).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
		}
	}
}
