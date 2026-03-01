namespace Repl;

public sealed partial class CoreReplApp
{
	private static HashSet<string> ResolveKnownHandlerOptionNames(
		Delegate handler,
		IEnumerable<string> routeValueNames)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ArgumentNullException.ThrowIfNull(routeValueNames);

		var routeNames = new HashSet<string>(routeValueNames, StringComparer.OrdinalIgnoreCase);
		var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var parameter in handler.Method.GetParameters())
		{
			if (string.IsNullOrWhiteSpace(parameter.Name)
				|| routeNames.Contains(parameter.Name)
				|| parameter.ParameterType == typeof(CancellationToken))
			{
				continue;
			}

			if (parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true).Length > 0
				|| parameter.GetCustomAttributes(typeof(FromServicesAttribute), inherit: true).Length > 0)
			{
				continue;
			}

			optionNames.Add(parameter.Name);
		}

		return optionNames;
	}
}
