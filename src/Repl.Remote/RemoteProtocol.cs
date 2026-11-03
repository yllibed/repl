using System.Text.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Repl.Remote;

/// <summary>Wire conventions shared by clients and engine adapters.</summary>
public static class RemoteProtocol
{
	/// <summary>Gets the experimental CLI profile version.</summary>
	public const string Version = "1.0.0";

	/// <summary>Gets the OpenRPC description of this profile.</summary>
	public static JsonElement Discover()
	{
		using var stream = typeof(RemoteProtocol).Assembly.GetManifestResourceStream("Repl.Remote.openrpc.json")
			?? throw new InvalidOperationException("The embedded remote CLI protocol description is missing.");
		using var document = JsonDocument.Parse(stream);
		return document.RootElement.Clone();
	}

	internal static JsonRpc CreateRpc(Stream input, Stream output, Action<Exception>? onError = null)
	{
		var formatter = new SystemTextJsonFormatter
		{
			JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
				PropertyNameCaseInsensitive = false,
			},
		};
		return new PortableJsonRpc(new HeaderDelimitedMessageHandler(new BorrowedStream(output), new BorrowedStream(input), formatter), onError)
		{
			CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
		};
	}

	/// <summary>
	/// Hands a failure the client sees only as a sanitized error to the operator. The trace keeps a record
	/// when no callback is supplied; the callback is application code, so its own failure is contained here
	/// rather than replacing the error the client is owed or faulting the connection.
	/// </summary>
	internal static void ReportFailure(Action<Exception>? onError, string message, Exception exception)
	{
		System.Diagnostics.Trace.TraceError(message, exception);
		if (onError is null) { return; }
		try
		{
			onError(exception);
		}
#pragma warning disable CA1031 // An operator callback must not turn a reported failure into a connection failure.
		catch (Exception callbackFailure)
#pragma warning restore CA1031
		{
			System.Diagnostics.Trace.TraceError("Remote CLI error callback failed: {0}", callbackFailure);
		}
	}

	private sealed class PortableJsonRpc(IJsonRpcMessageHandler handler, Action<Exception>? onError) : JsonRpc(handler)
	{
		protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
		{
			var error = base.CreateErrorDetails(request, exception);
			error.Data = null;
			if (exception is not (LocalRpcException or OperationCanceledException))
			{
				ReportFailure(onError, "Remote backend failure: {0}", exception);
				error.Code = JsonRpcErrorCode.InternalError;
				error.Message = "The remote backend failed.";
			}
			return error;
		}
	}

	internal static void RegisterTarget(JsonRpc rpc, object target)
	{
		foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (method.GetCustomAttribute<JsonRpcMethodAttribute>() is { } attribute)
			{
				rpc.AddLocalRpcMethod(method, method.IsStatic ? null : target, attribute);
			}
		}
	}
}
