using UniversalPresence.Media;

namespace UniversalPresence.Plugins;

public interface IUniversalPresencePlugin
{
    string Name { get; }

    void Initialize(PluginContext context);

    void OnTrackChanged(MediaSnapshot? snapshot);
}

public sealed class PluginContext
{
    public string DataDirectory { get; init; } = "";

    public Action<string> Log { get; init; } = _ => { };
}