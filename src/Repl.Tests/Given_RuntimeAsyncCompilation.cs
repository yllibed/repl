using System.Reflection;
using System.Runtime.CompilerServices;

namespace Repl.Tests;

[TestClass]
public sealed class Given_RuntimeAsyncCompilation
{
	private static readonly Assembly[] ReplAssemblies = [typeof(CoreReplApp).Assembly, typeof(ReplApp).Assembly];

#if REPL_RUNTIME_ASYNC
	[TestMethod]
	[Description("A build with ReplRuntimeAsync=true compiles Repl's async methods for the runtime instead of as compiler state machines.")]
	public void When_TheRuntimeAsyncSwitchIsOn_Then_ReplAsyncMethodsAreRuntimeAsync()
	{
		foreach (var assembly in ReplAssemblies)
		{
			var methods = DeclaredMethods(assembly);
			methods.Should().Contain(method => IsRuntimeAsyncMethod(method), assembly.GetName().Name);
			methods.Should().NotContain(method => HasCompilerStateMachine(method), assembly.GetName().Name);
		}
	}
#else
	[TestMethod]
	[Description("Without ReplRuntimeAsync, Repl keeps compiler state machines on every target, as its packages must (see src/Directory.Build.props).")]
	public void When_ReplIsBuiltForPackaging_Then_ItsAsyncMethodsKeepCompilerStateMachines()
	{
		foreach (var assembly in ReplAssemblies)
		{
			var methods = DeclaredMethods(assembly);
			methods.Should().Contain(method => HasCompilerStateMachine(method), assembly.GetName().Name);
			methods.Should().NotContain(method => IsRuntimeAsyncMethod(method), assembly.GetName().Name);
		}
	}
#endif

	private static MethodInfo[] DeclaredMethods(Assembly assembly) =>
	[
		.. assembly.GetTypes().SelectMany(type => type.GetMethods(
			BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)),
	];

	private static bool IsRuntimeAsyncMethod(MethodInfo method) =>
		method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Async);

	private static bool HasCompilerStateMachine(MethodInfo method) =>
		Attribute.IsDefined(method, typeof(AsyncStateMachineAttribute));
}
