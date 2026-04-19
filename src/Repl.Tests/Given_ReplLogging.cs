using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Repl.Tests;

[TestClass]
public sealed partial class Given_ReplLogging
{
	[TestMethod]
	[Description("Regression guard: verifies ReplApp registers Microsoft logging by default so ILogger<T> can be injected without extra setup.")]
	public void When_HandlerRequestsLogger_Then_DefaultAppCanResolveILogger()
	{
		var app = ReplApp.Create();
		app.Map("ping", (ILogger<Given_ReplLogging> logger) =>
		{
			LogMessages.PingHandled(logger);
			return "pong";
		});

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = app.Run(
			["ping", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("pong");
	}

	[TestMethod]
	[Description("Regression guard: verifies default logging stays silent until the app opts into a provider.")]
	public void When_DefaultAppLogsWithoutProvider_Then_NoUserFacingLogOutputIsProduced()
	{
		var app = ReplApp.Create();
		app.Map("status", (ILogger<Given_ReplLogging> logger) =>
		{
			LogMessages.StatusRequested(logger);
			return Results.Exit(0);
		});

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = app.Run(
			["status", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies Repl logging middleware opens a structured scope so session metadata is available to providers.")]
	public void When_HandlerLogsThroughILogger_Then_ReplScopeMetadataIsCaptured()
	{
		var provider = new CapturingLoggerProvider();
		var app = ReplApp.Create(services =>
		{
			services.AddSingleton(provider);
			services.AddLogging(builder =>
			{
				builder.ClearProviders();
				builder.AddProvider(provider);
			});
		});

		app.Map("status", (ILogger<Given_ReplLogging> logger) =>
		{
			LogMessages.StatusRequested(logger);
			return Results.Exit(0);
		});

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = app.Run(
			["status", "--no-logo"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		provider.Entries.Should().ContainSingle();
		var entry = provider.Entries[0];
		entry.Message.Should().Be("Status requested");
		entry.ScopeValues.Should().ContainKey("ReplSessionActive");
		entry.ScopeValues["ReplSessionActive"].Should().Be(expected: true);
		entry.ScopeValues.Should().ContainKey("ReplHostedSession");
		entry.ScopeValues["ReplHostedSession"].Should().Be(expected: true);
		entry.ScopeValues.Should().ContainKey("ReplProtocolPassthrough");
		entry.ScopeValues["ReplProtocolPassthrough"].Should().Be(expected: false);
	}

	[TestMethod]
	[Description("Regression guard: verifies ambient Repl log context reflects hosted session metadata so apps can route logs to the active session.")]
	public void When_HostedSessionRuns_Then_LogContextExposesSessionMetadata()
	{
		var app = ReplApp.Create();
		app.Map("context", (IReplLogContextAccessor accessor) =>
		{
			var current = accessor.Current;
			return new
			{
				current.IsSessionActive,
				current.IsHostedSession,
				current.IsProgrammatic,
				current.IsProtocolPassthrough,
				current.SessionId,
				current.TransportName,
				current.TerminalIdentity,
			};
		});

		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = app.Run(
			["context", "--json", "--no-logo"],
			host,
			new ReplRunOptions
			{
				HostedServiceLifecycle = HostedServiceLifecycleMode.None,
				TerminalOverrides = new TerminalSessionOverrides
				{
					TransportName = "websocket",
					TerminalIdentity = "xterm-256color",
				},
			});

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("\"isSessionActive\": true");
		output.ToString().Should().Contain("\"isHostedSession\": true");
		output.ToString().Should().Contain("\"transportName\": \"websocket\"");
		output.ToString().Should().Contain("\"terminalIdentity\": \"xterm-256color\"");
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}

	private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
	{
		private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

		public List<CapturedLogEntry> Entries { get; } = [];

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries, () => _scopeProvider);

		public void Dispose()
		{
		}

		public void SetScopeProvider(IExternalScopeProvider scopeProvider)
		{
			_scopeProvider = scopeProvider;
		}
	}

	private sealed class CapturingLogger(
		string categoryName,
		List<CapturedLogEntry> entries,
		Func<IExternalScopeProvider> scopeProviderAccessor) : ILogger
	{
		public IDisposable BeginScope<TState>(TState state)
			where TState : notnull =>
			scopeProviderAccessor().Push(state);

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			var scopes = new Dictionary<string, object?>(StringComparer.Ordinal);
			scopeProviderAccessor().ForEachScope(
				(scope, stateDictionary) =>
				{
					if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
					{
						foreach (var pair in kvps)
						{
							stateDictionary[pair.Key] = pair.Value;
						}
					}
				},
				scopes);

			entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception), scopes));
		}
	}

	private sealed record CapturedLogEntry(
		string Category,
		LogLevel Level,
		string Message,
		IReadOnlyDictionary<string, object?> ScopeValues);

	private static partial class LogMessages
	{
		[LoggerMessage(Level = LogLevel.Information, Message = "Ping handled")]
		public static partial void PingHandled(ILogger logger);

		[LoggerMessage(Level = LogLevel.Information, Message = "Status requested")]
		public static partial void StatusRequested(ILogger logger);
	}
}
