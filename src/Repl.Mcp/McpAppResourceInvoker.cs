using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Repl.Mcp;

internal static class McpAppResourceInvoker
{
	public static async ValueTask<string> InvokeAsync(
		Delegate handler,
		IServiceProvider services,
		McpAppResourceContext context,
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken)
	{
		var arguments = BindArguments(handler, services, context, request, cancellationToken);
		object? result;
		try
		{
			result = handler.DynamicInvoke(arguments);
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}

		return await ConvertResultAsync(result).ConfigureAwait(false);
	}

	private static object?[] BindArguments(
		Delegate handler,
		IServiceProvider services,
		McpAppResourceContext context,
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken)
	{
		var parameters = handler.Method.GetParameters();
		var arguments = new object?[parameters.Length];

		for (var i = 0; i < parameters.Length; i++)
		{
			arguments[i] = BindArgument(parameters[i], services, context, request, cancellationToken);
		}

		return arguments;
	}

	private static object? BindArgument(
		ParameterInfo parameter,
		IServiceProvider services,
		McpAppResourceContext context,
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken)
	{
		if (parameter.ParameterType == typeof(McpAppResourceContext))
		{
			return context;
		}

		if (parameter.ParameterType == typeof(RequestContext<ReadResourceRequestParams>))
		{
			return request;
		}

		if (parameter.ParameterType == typeof(CancellationToken))
		{
			return cancellationToken;
		}

		if (parameter.ParameterType == typeof(IServiceProvider))
		{
			return services;
		}

		var service = services.GetService(parameter.ParameterType);
		if (service is not null)
		{
			return service;
		}

		if (parameter.HasDefaultValue)
		{
			return parameter.DefaultValue;
		}

		throw new InvalidOperationException(
			$"Cannot resolve MCP App UI resource parameter '{parameter.Name}' of type '{parameter.ParameterType}'.");
	}

	private static async ValueTask<string> ConvertResultAsync(object? result)
	{
		switch (result)
		{
			case null:
				return string.Empty;
			case string text:
				return text;
			case ValueTask<string> valueTask:
				return await valueTask.ConfigureAwait(false);
			case Task<string> task:
				return await task.ConfigureAwait(false);
			case Task task:
				await task.ConfigureAwait(false);
				return string.Empty;
			default:
				return result.ToString() ?? string.Empty;
		}
	}
}
