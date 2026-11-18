using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.Karaoke.Services;

/// <summary>
/// Keeps an optional playlist in step with the songs that have AI stems.
/// The playlist is public, so every user sees it; it belongs to the configured user
/// (or the first administrator). Turning the option off stops updates but leaves the playlist alone.
/// </summary>
public class PlaylistSync : IHostedService
{
    private readonly StemStore _store;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<PlaylistSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlaylistSync(
        StemStore store,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<PlaylistSync> logger)
    {
        _store = store;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => Trigger();
        }

        // Give the library a moment to finish loading before the first pass.
        _ = Task.Delay(TimeSpan.FromMinutes(1), CancellationToken.None).ContinueWith(_ => Trigger(), TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs a sync in the background; errors are logged, never thrown.</summary>
    public void Trigger()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Karaoke playlist sync failed");
            }
        });
    }

    /// <summary>Brings the playlist up to date. Returns a short description of what happened.</summary>
    public async Task<string> SyncAsync()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.MaintainPlaylist)
        {
            return "The playlist option is off.";
        }

        var name = string.IsNullOrWhiteSpace(config.PlaylistName) ? "Karaoke Ready" : config.PlaylistName.Trim();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ownerId = ResolveOwner(config.PlaylistUserId);
            if (ownerId == Guid.Empty)
            {
                return "No user to own the playlist was found.";
            }

            // Songs with stems that are still in the library, in artist / album / track order.
            var ready = _store.GetReadyIds()
                .Select(id => Guid.TryParse(id, out var g) ? _libraryManager.GetItemById(g) as AudioItem : null)
                .Where(i => i is not null)
                .Select(i => i!)
                .OrderBy(i => i.AlbumArtists?.FirstOrDefault() ?? i.Artists?.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Album ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.ParentIndexNumber ?? 0)
                .ThenBy(i => i.IndexNumber ?? 0)
                .ToList();

            var playlist = _playlistManager.GetPlaylists(ownerId)
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (playlist is null)
            {
                await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = name,
                    ItemIdList = ready.Select(i => i.Id).ToList(),
                    MediaType = MediaType.Audio,
                    UserId = ownerId,
                    Public = true
                }).ConfigureAwait(false);

                _logger.LogInformation("Created karaoke playlist {Name} with {Count} songs", name, ready.Count);
                return string.Create(CultureInfo.InvariantCulture, $"Created \"{name}\" with {ready.Count} songs.");
            }

            var wanted = ready.Select(i => i.Id).ToHashSet();
            var entries = playlist.GetManageableItems();
            var present = entries.Select(e => e.Item2.Id).ToHashSet();

            var toRemove = entries
                .Where(e => !wanted.Contains(e.Item2.Id) && e.Item1.ItemId.HasValue)
                .Select(e => e.Item1.ItemId!.Value.ToString("N", CultureInfo.InvariantCulture))
                .ToList();
            var toAdd = ready.Where(i => !present.Contains(i.Id)).Select(i => i.Id).ToList();

            if (toRemove.Count > 0)
            {
                await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N", CultureInfo.InvariantCulture), toRemove).ConfigureAwait(false);
            }

            if (toAdd.Count > 0)
            {
                await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, null, ownerId).ConfigureAwait(false);
            }

            if (toAdd.Count > 0 || toRemove.Count > 0)
            {
                _logger.LogInformation("Karaoke playlist {Name}: added {Added}, removed {Removed}", name, toAdd.Count, toRemove.Count);
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"\"{name}\" is up to date: {wanted.Count} songs ({toAdd.Count} added, {toRemove.Count} removed).");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Guid ResolveOwner(string? configured)
    {
        if (Guid.TryParse(configured, out var id) && _userManager.GetUserById(id) is not null)
        {
            return id;
        }

        var admin = _userManager.GetUsers().FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
        return admin?.Id ?? Guid.Empty;
    }
}
