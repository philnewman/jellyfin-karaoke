using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Karaoke.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the karaoke script is added to the web client's index.html.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a song is queued for AI separation the first time karaoke mode is used on it.
    /// </summary>
    public bool AutoQueueOnUse { get; set; } = true;

    /// <summary>
    /// Gets or sets how long a worker may hold a job before it is handed out again.
    /// </summary>
    public int ClaimTimeoutMinutes { get; set; } = 240;
}
