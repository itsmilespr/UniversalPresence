using System.Reflection;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Plugins;

public sealed class PluginHost
{
    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly List<IUniversalPresencePlugin> _plugins = new();

    public PluginHost(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        if (options.PluginsEnabled)
        {
            Load();
        }
    }

    public IReadOnlyList<string> PluginNames => _plugins.Select(p => p.Name).ToList();

    public void Notify(MediaSnapshot? snapshot)
    {
        foreach (var plugin in _plugins)
        {
            try { plugin.OnTrackChanged(snapshot); }
            catch (Exception ex) { _log.Warn($"Plugin '{plugin.Name}' failed: {ex.Message}"); }
        }
    }

    private void Load()
    {
        var directory = Path.Combine(AppOptions.LocalDataDirectory, "plugins");
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                foreach (var type in assembly.GetTypes())
                {
                    if (!typeof(IUniversalPresencePlugin).IsAssignableFrom(type) || type.IsAbstract) continue;
                    if (Activator.CreateInstance(type) is not IUniversalPresencePlugin plugin) continue;
                    plugin.Initialize(new PluginContext
                    {
                        DataDirectory = AppOptions.LocalDataDirectory,
                        Log = message => _log.Info($"[plugin:{plugin.Name}] {message}"),
                    });
                    _plugins.Add(plugin);
                    _log.Info($"Plugin loaded: {plugin.Name} ({Path.GetFileName(file)})");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not load plugin '{Path.GetFileName(file)}': {ex.Message}");
            }
        }
    }
}