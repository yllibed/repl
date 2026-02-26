using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Repl;

internal static class CommandInvoker
{
	private delegate object? RawHandlerInvoker(object? target, object?[] arguments);

	private readonly record struct ValueTaskAdapter(
		Func<object, Task> AsTask,
		Func<Task, object?> GetResult);

	private static readonly ConcurrentDictionary<MethodInfo, RawHandlerInvoker> RawInvokers = new();
	private static readonly ConcurrentDictionary<Type, Func<Task, object?>> TaskResultReaders = new();
	private static readonly ConcurrentDictionary<Type, ValueTaskAdapter> ValueTaskAdapters = new();

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Command invocation supports delegates returning Task<T>/ValueTask<T> with runtime result extraction.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2067",
		Justification = "Runtime command invocation resolves async generic wrappers from runtime types.")]
	public static async ValueTask<object?> InvokeAsync(
		Delegate handler,
		object?[] arguments)
	{
		ArgumentNullException.ThrowIfNull(handler);
		ArgumentNullException.ThrowIfNull(arguments);

		var method = handler.Method;
		var invoker = RawInvokers.GetOrAdd(method, static m => CreateRawInvoker(m));
		var result = invoker(method.IsStatic ? null : handler.Target, arguments);
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
			var adapter = ValueTaskAdapters.GetOrAdd(result.GetType(), static type => CreateValueTaskAdapter(type));
			var asTask = adapter.AsTask(result);
			await asTask.ConfigureAwait(false);
			return adapter.GetResult(asTask);
		}

		return result;
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Task<T>/ValueTask<T> result extraction is a runtime invocation feature.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Task/ValueTask result extraction uses runtime reflection against handler return types.")]
	[UnconditionalSuppressMessage(
		"AOT",
		"IL3050",
		Justification = "Runtime command invocation compiles delegates for handler methods and async result adapters.")]
	private static RawHandlerInvoker CreateRawInvoker(MethodInfo method)
	{
		ArgumentNullException.ThrowIfNull(method);
		var parameters = method.GetParameters();
		var targetParameter = Expression.Parameter(typeof(object), "target");
		var argumentsParameter = Expression.Parameter(typeof(object[]), "arguments");
		var castArguments = new Expression[parameters.Length];
		for (var index = 0; index < parameters.Length; index++)
		{
			var parameterType = parameters[index].ParameterType;
			if (parameterType.IsByRef)
			{
				throw new NotSupportedException("Handlers with by-ref parameters are not supported.");
			}

			var rawArgument = Expression.ArrayIndex(argumentsParameter, Expression.Constant(index));
			castArguments[index] = Expression.Convert(rawArgument, parameterType);
		}

		var call = method.IsStatic
			? Expression.Call(method, castArguments)
			: Expression.Call(Expression.Convert(targetParameter, method.DeclaringType!), method, castArguments);
		Expression body = method.ReturnType == typeof(void)
			? Expression.Block(call, Expression.Constant(null, typeof(object)))
			: Expression.Convert(call, typeof(object));
		return Expression.Lambda<RawHandlerInvoker>(body, targetParameter, argumentsParameter).Compile();
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Task<T> result extraction is a runtime invocation feature.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2067",
		Justification = "Task result reader cache is built from runtime task types.")]
	private static object? TryGetTaskResult(Task task)
	{
		var taskType = task.GetType();
		var reader = TaskResultReaders.GetOrAdd(taskType, static type => CreateTaskResultReader(type));
		return reader(task);
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "Task<T> result extraction is a runtime invocation feature.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "Task<T> result member lookup is performed from runtime task type metadata.")]
	[UnconditionalSuppressMessage(
		"AOT",
		"IL3050",
		Justification = "Runtime command invocation compiles async result readers for Task<T>/ValueTask<T>.")]
	private static Func<Task, object?> CreateTaskResultReader(Type taskType)
	{
		ArgumentNullException.ThrowIfNull(taskType);
		var resultProperty = taskType.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
		if (resultProperty is null)
		{
			return static _ => null;
		}

		var taskParameter = Expression.Parameter(typeof(Task), "task");
		var castTask = Expression.Convert(taskParameter, taskType);
		var readResult = Expression.Property(castTask, resultProperty);
		var box = Expression.Convert(readResult, typeof(object));
		return Expression.Lambda<Func<Task, object?>>(box, taskParameter).Compile();
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2075",
		Justification = "ValueTask<T>.AsTask resolution is a runtime invocation feature.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2070",
		Justification = "ValueTask<T>.AsTask lookup is performed from runtime value task type metadata.")]
	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2072",
		Justification = "ValueTask<T>.AsTask return type is runtime-derived and used for cached Task result readers.")]
	[UnconditionalSuppressMessage(
		"AOT",
		"IL3050",
		Justification = "Runtime command invocation compiles async result readers for Task<T>/ValueTask<T>.")]
	private static ValueTaskAdapter CreateValueTaskAdapter(Type valueTaskType)
	{
		ArgumentNullException.ThrowIfNull(valueTaskType);
		var asTaskMethod = valueTaskType.GetMethod(name: "AsTask", types: Type.EmptyTypes)
			?? throw new InvalidOperationException("Unable to invoke ValueTask<T>.AsTask().");
		var boxedValueTask = Expression.Parameter(typeof(object), "valueTask");
		var asTaskCall = Expression.Call(Expression.Convert(boxedValueTask, valueTaskType), asTaskMethod);
		var asTask = Expression.Convert(asTaskCall, typeof(Task));
		var asTaskInvoker = Expression.Lambda<Func<object, Task>>(asTask, boxedValueTask).Compile();
		return new ValueTaskAdapter(asTaskInvoker, CreateTaskResultReader(asTaskMethod.ReturnType));
	}
}
