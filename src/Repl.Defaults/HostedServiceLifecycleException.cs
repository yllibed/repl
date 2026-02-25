namespace Repl;

internal sealed class HostedServiceLifecycleException(string message, Exception innerException)
	: Exception(message, innerException)
{
}
