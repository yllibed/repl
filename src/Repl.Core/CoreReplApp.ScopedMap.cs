using System.ComponentModel;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private sealed class ScopedMap(CoreReplApp app, string prefix) : ICoreReplApp
	{
		private readonly CoreReplApp _app = app;
		private readonly string _prefix = prefix;

		public CommandBuilder Map(string route, Delegate handler)
		{
			route = string.IsNullOrWhiteSpace(route)
				? throw new ArgumentException("Route cannot be empty.", nameof(route))
				: route;

			var fullRoute = string.Concat(_prefix, " ", route).Trim();
			return _app.Map(fullRoute, handler);
		}

		public ICoreReplApp Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			_app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix);
			configure(childMap);
			return this;
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

		IReplMap IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			_app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix);
			configure(childMap);
			return this;
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
			var context = _app._contexts.LastOrDefault(c =>
				string.Equals(c.Template.Template, _prefix, StringComparison.OrdinalIgnoreCase));
			if (context is not null)
			{
				context.Banner = bannerProvider;
			}
		}
	}
}
