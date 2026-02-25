using System.Diagnostics.CodeAnalysis;

namespace Repl;

internal static class CommandInvoker
{
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Command invocation supports delegates returning Task<T>/ValueTask<T> with runtime result extraction.")]
	public static async ValueTask<object?> InvokeAsync(
		Delegate handler,
		object?[] arguments)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ArgumentNullException.ThrowIfNull(arguments);

		var result = handler.DynamicInvoke(arguments);
		if (result is Task task)
		{
			await task.ConfigureAwait(false);
			return TryGetTaskResult(task);
		}

		if (result is ValueTask valueTask)
		{
			await valueTask.ConfigureAwait(false);
			return null;
		}

		if (result is not null
			&& result.GetType().IsGenericType
			&& result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
		{
			var asTaskMethod = result.GetType().GetMethod(name: "AsTask", types: Type.EmptyTypes)
				?? throw new InvalidOperationException("Unable to invoke ValueTask<T>.AsTask().");
				var asTask = (Task?)asTaskMethod.Invoke(obj: result, parameters: null)
					?? throw new InvalidOperationException("Unable to convert ValueTask<T> to Task.");
			await asTask.ConfigureAwait(false);
			return TryGetTaskResult(asTask);
		}

		return result;
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Task<T> result extraction is a runtime invocation feature.")]
	private static object? TryGetTaskResult(Task task)
	{
		var taskType = task.GetType();
		var resultProperty = taskType.GetProperty("Result");
		return resultProperty?.GetValue(task);
	}
}
