using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Karaoke.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Karaoke;

/// <summary>
/// Karaoke mode plugin: serves the web-client script, tracks which songs have
/// AI-separated stems, and hands separation jobs to an external worker.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Karaoke";

    public override string Description => "Adds a karaoke toggle and vocal level slider to the web music player.";

    public override Guid Id => Guid.Parse("7c1b3f0e-5d2a-4e8b-9a6f-2f4d8c1e7b53");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = "Jellyfin.Plugin.Karaoke.Configuration.configPage.html"
            }
        };
    }
}
