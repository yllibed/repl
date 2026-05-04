namespace Repl;

internal sealed class ImplicitServiceParameterRegistry
{
	private readonly HashSet<Type> _globalOptionsTypes = [];

	public void AddGlobalOptionsType(Type optionsType)
	{
		ArgumentNullException.ThrowIfNull(optionsType);
		_globalOptionsTypes.Add(optionsType);
	}

	public bool IsImplicitServiceParameter(Type parameterType) =>
		IsFrameworkInjectedParameter(parameterType)
		|| TryGetGlobalOptionsServiceType(parameterType, out _);

	public bool TryGetGlobalOptionsServiceType(Type parameterType, out Type serviceType)
	{
		ArgumentNullException.ThrowIfNull(parameterType);

		serviceType = typeof(void);
		if (_globalOptionsTypes.Contains(parameterType))
		{
			serviceType = parameterType;
			return true;
		}

		if (parameterType == typeof(object))
		{
			return false;
		}

		var matches = _globalOptionsTypes
			.Where(parameterType.IsAssignableFrom)
			.OrderBy(static type => type.FullName, StringComparer.Ordinal)
			.ToArray();
		if (matches.Length == 0)
		{
			return false;
		}

		if (matches.Length > 1)
		{
			throw new InvalidOperationException(
				$"Ambiguous typed global options binding for parameter type '{parameterType.Name}'. "
				+ $"Registered matching types: {string.Join(", ", matches.Select(static type => type.Name))}. "
				+ "Use the concrete registered options type or an explicit [FromServices] parameter.");
		}

		serviceType = matches[0];
		return true;
	}

	private static bool IsFrameworkInjectedParameter(Type parameterType) =>
		parameterType == typeof(IServiceProvider)
		|| parameterType == typeof(ICoreReplApp)
		|| parameterType == typeof(CoreReplApp)
		|| parameterType == typeof(IGlobalOptionsAccessor)
		|| parameterType == typeof(IReplSessionState)
		|| parameterType == typeof(IReplInteractionChannel)
		|| parameterType == typeof(IReplIoContext)
		|| parameterType == typeof(IReplKeyReader)
		|| parameterType == typeof(IReplPagingContext)
		|| string.Equals(parameterType.FullName, "Repl.Mcp.IMcpClientRoots", StringComparison.Ordinal)
		|| string.Equals(parameterType.FullName, "Repl.Mcp.IMcpSampling", StringComparison.Ordinal)
		|| string.Equals(parameterType.FullName, "Repl.Mcp.IMcpElicitation", StringComparison.Ordinal)
		|| string.Equals(parameterType.FullName, "Repl.Mcp.IMcpFeedback", StringComparison.Ordinal);
}
