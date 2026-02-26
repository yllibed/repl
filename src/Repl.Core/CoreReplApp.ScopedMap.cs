using System.ComponentModel;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private sealed class ScopedMap(CoreReplApp app, string prefix, ContextDefinition context) : ICoreReplApp
	{
		private readonly CoreReplApp _app = app;
		private readonly string _prefix = prefix;
		private readonly ContextDefinition _context = context;

		public CommandBuilder Map(string route, Delegate handler)
		{
			route = string.IsNullOrWhiteSpace(route)
				? throw new ArgumentException("Route cannot be empty.", nameof(route))
				: route;

			var fullRoute = string.Concat(_prefix, " ", route).Trim();
			return _app.Map(fullRoute, handler);
		}

		public IContextBuilder Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			var childContext = _app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix, childContext);
			configure(childMap);
			return new ContextBuilder(this, childContext);
		}

		public ScopedMap MapModule(IReplModule module)
		{
			ArgumentNullException.ThrowIfNull(module);
			_app.MapModuleCore(module, static _ => true, this);
			return this;
		}

		public ScopedMap MapModule(
			IReplModule module,
			Func<ModulePresenceContext, bool> isPresent)
		{
			ArgumentNullException.ThrowIfNull(module);
			ArgumentNullException.ThrowIfNull(isPresent);
			_app.MapModuleCore(module, isPresent, this);
			return this;
		}

		IContextBuilder IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			var childContext = _app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix, childContext);
			configure(childMap);
			return new ContextBuilder(this, childContext);
		}

		IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

		IReplMap IReplMap.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
			MapModule(module, isPresent);

		ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

		ICoreReplApp ICoreReplApp.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
			MapModule(module, isPresent);

		ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider)
		{
			SetBannerOnContext(bannerProvider);
			return this;
		}

		IReplMap IReplMap.WithBanner(Delegate bannerProvider)
		{
			SetBannerOnContext(bannerProvider);
			return this;
		}

		ICoreReplApp ICoreReplApp.WithBanner(string text)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text);
			SetBannerOnContext(() => text);
			return this;
		}

		IReplMap IReplMap.WithBanner(string text)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text);
			SetBannerOnContext(() => text);
			return this;
		}

		private void SetBannerOnContext(Delegate bannerProvider)
		{
			ArgumentNullException.ThrowIfNull(bannerProvider);
			_context.Banner = bannerProvider;
		}
	}

	private sealed class ContextBuilder(IReplMap parentMap, ContextDefinition context) : IContextBuilder
	{
		private readonly IReplMap _parentMap = parentMap;
		private readonly ContextDefinition _context = context;

		public CommandBuilder Map(string route, Delegate handler) => _parentMap.Map(route, handler);

		public IContextBuilder Context(string segment, Action<IReplMap> configure, Delegate? validation = null) =>
			_parentMap.Context(segment, configure, validation);

		public IReplMap MapModule(IReplModule module) => _parentMap.MapModule(module);

		public IReplMap MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
			_parentMap.MapModule(module, isPresent);

		public IReplMap WithBanner(Delegate bannerProvider) => _parentMap.WithBanner(bannerProvider);

		public IReplMap WithBanner(string text) => _parentMap.WithBanner(text);

		public IContextBuilder Hidden(bool isHidden = true)
		{
			_context.IsHidden = isHidden;
			return this;
		}
	}
}
