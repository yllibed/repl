using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Repl.Documentation;
using Repl.Rendering;

namespace Repl.Remote;

/// <summary>Adapts an existing ReplApp, borrowing its application and root service provider.</summary>
public sealed class ReplAppRemoteBackend : IRemoteReplBackend
{
	private readonly ReplApp _app;
	private readonly IServiceProvider _services;

	/// <summary>Creates an adapter. The caller retains application and root-provider ownership.</summary>
	public ReplAppRemoteBackend(ReplApp app, IServiceProvider? services = null)
	{
		ArgumentNullException.ThrowIfNull(app);
		_app = app;
		_services = services ?? app.Services;
	}

	/// <inheritdoc />
	public async ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var scope = _services.CreateAsyncScope();
		await using var scopeOwner = scope.ConfigureAwait(false);
		using var session = OpenProgrammaticSession(TextWriter.Null, TextWriter.Null, TextWriter.Null);
		var model = _app.Core.CreateDocumentationModel(scope.ServiceProvider);
		return new(model.App.Name,
			model.Commands.Where(command => !command.IsHidden).Select(command => new RemoteCommand(
				command.Path, command.Description, command.Aliases,
				[.. command.Arguments.Select(argument => new RemoteParameter(argument.Name, "argument", argument.Required,
					argument.Description, ParameterSchema.Create(argument.Type), [])),
				.. command.Options.Where(option => !option.IsHidden).Select(option => new RemoteParameter(
					DocumentationEngine.ResolveInvocationParameterName(option), "option", option.Required, option.Description,
					ParameterSchema.Create(DocumentationEngine.ResolveInvocationParameterType(option) ?? typeof(object)), option.Aliases)),])).ToArray(),
			model.Contexts.Where(context => !context.IsHidden).Select(context => new RemoteContext(context.Path, context.Description)).ToArray())
		{
			GlobalParameters = _app.Core.OptionsSnapshot.Parsing.GlobalOptions.Values.Where(option => !option.IsHidden)
				.Select(option => new RemoteParameter(option.Name, "option", Required: false, option.Description,
					ParameterSchema.Create(option.ValueType), [option.CanonicalToken, .. option.Aliases])).ToArray(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		var scope = _services.CreateAsyncScope();
		await using var scopeOwner = scope.ConfigureAwait(false);
		using var output = new StringWriter(CultureInfo.InvariantCulture);
		using var error = new StringWriter(CultureInfo.InvariantCulture);
		using var rendered = new StringWriter(CultureInfo.InvariantCulture);
		using var session = OpenProgrammaticSession(rendered, output, error);
		using var invocationContract = ReplSessionIO.PushProgrammaticInvocationContract(2);
		var invocation = request.Arguments is { } arguments ? ReplInvocationRequest.FromArguments(arguments) :
			ReplInvocationRequest.ForCommand(request.Command!, ToValues(request.Values), ToValues(request.GlobalValues));
		var outcome = await _app.Core.RunInvocationWithOutcomeAsync(invocation,
			["--output:json", "--no-interactive"], scope.ServiceProvider, presenceServiceProvider: null,
			ReplInvocationChannel.Programmatic, useApplicationExitCodes: true, cancellationToken).ConfigureAwait(false);
		return new(outcome.ExitCode, JsonNamingPolicy.CamelCase.ConvertName(outcome.Kind.ToString()),
			ParseRenderedResult(rendered.ToString()), output.ToString(), error.ToString());
	}

	private static Dictionary<string, object?>? ToValues(IReadOnlyDictionary<string, JsonElement>? values) =>
		values?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);

	private static IDisposable OpenProgrammaticSession(TextWriter rendered, TextWriter output, TextWriter error)
	{
		var sessionId = $"remote-{Guid.NewGuid():N}";
		var session = ReplSessionIO.SetSession(rendered, TextReader.Null, AnsiMode.Never,
			sessionId, output, error, removeSessionOnDispose: true);
		ReplSessionIO.IsProgrammatic = true;
		ReplSessionIO.UpdateSession(sessionId, metadata => metadata with { TransportName = "remote-cli" });
		return session;
	}

	private static JsonElement? ParseRenderedResult(string rendered)
	{
		if (string.IsNullOrWhiteSpace(rendered)) { return null; }
		var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(rendered), new JsonReaderOptions { AllowMultipleValues = true });
		var values = new List<JsonElement>();
		while (reader.Read())
		{
			using var document = JsonDocument.ParseValue(ref reader);
			values.Add(document.RootElement.Clone());
		}
		return values.Count == 1 ? values[0] : JsonSerializer.SerializeToElement(values);
	}

	/// <inheritdoc />
	public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options,
		Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) =>
		ValueTask.FromResult<IRemoteTerminalSession>(new ReplAppTerminal(_app, _services, options, writeOutput, cancellationToken));

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
