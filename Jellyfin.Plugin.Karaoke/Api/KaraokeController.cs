using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Karaoke.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.Karaoke.Api;

/// <summary>
/// Endpoints used by the web-client script (State, Status, Queue, Stems) and by
/// the separation worker (Jobs/*). Worker and admin endpoints need an admin token or API key.
/// </summary>
[ApiController]
[Route("Karaoke")]
[Authorize]
public class KaraokeController : ControllerBase
{
    private const string Elevated = "RequiresElevation";

    private readonly StemStore _store;
    private readonly ILibraryManager _libraryManager;

    public KaraokeController(StemStore store, ILibraryManager libraryManager)
    {
        _store = store;
        _libraryManager = libraryManager;
    }

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult GetClientScript()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Karaoke.Web.karaoke.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript; charset=utf-8");
    }

    [HttpGet("State")]
    public ActionResult<StateDto> GetState()
    {
        return new StateDto
        {
            AutoQueue = Plugin.Instance?.Configuration.AutoQueueOnUse ?? true,
            Ids = _store.GetReadyIds()
        };
    }

    [HttpGet("Items/{itemId}/Status")]
    public ActionResult<StatusDto> GetStatus([FromRoute] Guid itemId)
    {
        var (state, error) = _store.GetState(itemId);
        return new StatusDto { State = state, Error = error };
    }

    /// <summary>Queues one song, or every song in an album, playlist or folder.</summary>
    [HttpPost("Items/{itemId}/Queue")]
    public ActionResult<QueueResultDto> Queue([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        IEnumerable<AudioItem> songs = item switch
        {
            AudioItem audio => new[] { audio },
            Playlist playlist => playlist.GetManageableItems().Select(t => t.Item2).OfType<AudioItem>(),
            Folder folder => folder.GetRecursiveChildren(i => i is AudioItem, true).OfType<AudioItem>(),
            _ => Array.Empty<AudioItem>()
        };

        var queued = 0;
        foreach (var song in songs)
        {
            if (!string.IsNullOrEmpty(song.Path) && _store.Enqueue(song.Id, song.Name ?? string.Empty))
            {
                queued++;
            }
        }

        var (state, _) = _store.GetState(itemId);
        return new QueueResultDto { Queued = queued, State = state };
    }

    [HttpGet("Items/{itemId}/Stems")]
    public ActionResult GetStems([FromRoute] Guid itemId)
    {
        var path = _store.GetStemPath(itemId);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "audio/webm", true);
    }

    [HttpDelete("Items/{itemId}/Stems")]
    [Authorize(Policy = Elevated)]
    public ActionResult DeleteStems([FromRoute] Guid itemId)
    {
        _store.Remove(itemId);
        return NoContent();
    }

    [HttpGet("Queue")]
    [Authorize(Policy = Elevated)]
    public ActionResult<QueueDto> GetQueue()
    {
        return new QueueDto
        {
            ReadyCount = _store.GetReadyIds().Count,
            Jobs = _store.GetJobs()
        };
    }

    [HttpPost("Jobs/Claim")]
    [Authorize(Policy = Elevated)]
    public ActionResult<ClaimDto> Claim()
    {
        var minutes = Math.Max(5, Plugin.Instance?.Configuration.ClaimTimeoutMinutes ?? 240);

        while (true)
        {
            var job = _store.Claim(TimeSpan.FromMinutes(minutes));
            if (job is null)
            {
                return NoContent();
            }

            var item = _libraryManager.GetItemById(job.ItemId);
            if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
            {
                _store.Fail(job.ItemId, "The song's file is no longer in the library.");
                continue;
            }

            return new ClaimDto
            {
                ItemId = job.ItemId.ToString("N"),
                Name = job.Name,
                Extension = Path.GetExtension(item.Path)
            };
        }
    }

    [HttpGet("Jobs/{itemId}/Source")]
    [Authorize(Policy = Elevated)]
    public ActionResult GetSource([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        return PhysicalFile(item.Path, "application/octet-stream", true);
    }

    [HttpPost("Jobs/{itemId}/Complete")]
    [Authorize(Policy = Elevated)]
    [RequestSizeLimit(536870912)]
    public async Task<ActionResult> Complete([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await _store.CompleteAsync(itemId, Request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }

    [HttpPost("Jobs/{itemId}/Fail")]
    [Authorize(Policy = Elevated)]
    public async Task<ActionResult> Fail([FromRoute] Guid itemId)
    {
        string error;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            error = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (error.Length > 500)
        {
            error = error.Substring(0, 500);
        }

        _store.Fail(itemId, string.IsNullOrWhiteSpace(error) ? "Unknown worker error." : error.Trim());
        return NoContent();
    }

    public class StateDto
    {
        public bool AutoQueue { get; set; }

        public IReadOnlyList<string> Ids { get; set; } = Array.Empty<string>();
    }

    public class StatusDto
    {
        public string State { get; set; } = "none";

        public string? Error { get; set; }
    }

    public class QueueResultDto
    {
        public int Queued { get; set; }

        public string State { get; set; } = "none";
    }

    public class QueueDto
    {
        public int ReadyCount { get; set; }

        public IReadOnlyList<KaraokeJob> Jobs { get; set; } = Array.Empty<KaraokeJob>();
    }

    public class ClaimDto
    {
        public string ItemId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Extension { get; set; } = string.Empty;
    }
}
