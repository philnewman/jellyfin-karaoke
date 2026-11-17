using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Karaoke.Services;

public enum JobState
{
    Queued,
    Processing,
    Failed
}

public class KaraokeJob
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public JobState State { get; set; }

    public string? Error { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? ClaimedAt { get; set; }

    public int Attempts { get; set; }
}

/// <summary>
/// Owns the stem files on disk and the queue of songs waiting to be separated.
/// A song is "ready" exactly when its stem file exists.
/// </summary>
public class StemStore
{
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly string _stemDir;
    private readonly string _jobsFile;
    private readonly ILogger<StemStore> _logger;
    private readonly Dictionary<Guid, KaraokeJob> _jobs;

    public StemStore(IApplicationPaths applicationPaths, ILogger<StemStore> logger)
    {
        _logger = logger;
        var root = Path.Combine(applicationPaths.DataPath, "karaoke");
        _stemDir = Path.Combine(root, "stems");
        _jobsFile = Path.Combine(root, "jobs.json");
        Directory.CreateDirectory(_stemDir);
        _jobs = Load();
    }

    public string GetStemPath(Guid itemId)
        => Path.Combine(_stemDir, itemId.ToString("N", CultureInfo.InvariantCulture) + ".webm");

    public bool IsReady(Guid itemId) => File.Exists(GetStemPath(itemId));

    public IReadOnlyList<string> GetReadyIds()
    {
        return Directory.EnumerateFiles(_stemDir, "*.webm")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>Returns "ready", "queued", "processing", "failed" or "none", plus any error text.</summary>
    public (string State, string? Error) GetState(Guid itemId)
    {
        if (IsReady(itemId))
        {
            return ("ready", null);
        }

        lock (_lock)
        {
            if (_jobs.TryGetValue(itemId, out var job))
            {
                return (job.State.ToString().ToLowerInvariant(), job.Error);
            }
        }

        return ("none", null);
    }

    public IReadOnlyList<KaraokeJob> GetJobs()
    {
        lock (_lock)
        {
            return _jobs.Values.OrderBy(j => j.QueuedAt).ToList();
        }
    }

    /// <summary>Queues a song. Returns false if it is already ready or already waiting.</summary>
    public bool Enqueue(Guid itemId, string name)
    {
        if (IsReady(itemId))
        {
            return false;
        }

        lock (_lock)
        {
            if (_jobs.TryGetValue(itemId, out var existing) && existing.State != JobState.Failed)
            {
                return false;
            }

            _jobs[itemId] = new KaraokeJob
            {
                ItemId = itemId,
                Name = name,
                State = JobState.Queued,
                QueuedAt = DateTime.UtcNow
            };
            Save();
            return true;
        }
    }

    /// <summary>Hands the oldest waiting job to a worker, or null if there is nothing to do.</summary>
    public KaraokeJob? Claim(TimeSpan claimTimeout)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var changed = false;

            // Jobs a worker took and never finished go back in the queue, a limited number of times.
            foreach (var job in _jobs.Values.Where(j => j.State == JobState.Processing).ToList())
            {
                if (job.ClaimedAt is null || now - job.ClaimedAt.Value > claimTimeout)
                {
                    if (job.Attempts >= MaxAttempts)
                    {
                        job.State = JobState.Failed;
                        job.Error = "Worker did not finish after " + MaxAttempts + " attempts.";
                    }
                    else
                    {
                        job.State = JobState.Queued;
                    }

                    changed = true;
                }
            }

            var next = _jobs.Values
                .Where(j => j.State == JobState.Queued)
                .OrderBy(j => j.QueuedAt)
                .FirstOrDefault();

            if (next is not null)
            {
                next.State = JobState.Processing;
                next.ClaimedAt = now;
                next.Attempts++;
                next.Error = null;
                changed = true;
            }

            if (changed)
            {
                Save();
            }

            return next;
        }
    }

    public async Task CompleteAsync(Guid itemId, Stream content, CancellationToken cancellationToken)
    {
        var finalPath = GetStemPath(itemId);
        var tempPath = finalPath + ".part";

        var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await using (stream.ConfigureAwait(false))
        {
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (new FileInfo(tempPath).Length == 0)
        {
            File.Delete(tempPath);
            throw new InvalidDataException("Uploaded stem file was empty.");
        }

        File.Move(tempPath, finalPath, true);

        lock (_lock)
        {
            if (_jobs.Remove(itemId))
            {
                Save();
            }
        }

        _logger.LogInformation("Karaoke stems stored for item {ItemId}", itemId);
    }

    public void Fail(Guid itemId, string error)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(itemId, out var job))
            {
                job.State = JobState.Failed;
                job.Error = error;
                Save();
            }
        }

        _logger.LogWarning("Karaoke separation failed for item {ItemId}: {Error}", itemId, error);
    }

    /// <summary>Removes the stem file and any queue entry for a song.</summary>
    public void Remove(Guid itemId)
    {
        var path = GetStemPath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        lock (_lock)
        {
            if (_jobs.Remove(itemId))
            {
                Save();
            }
        }
    }

    private Dictionary<Guid, KaraokeJob> Load()
    {
        try
        {
            if (File.Exists(_jobsFile))
            {
                var list = JsonSerializer.Deserialize<List<KaraokeJob>>(File.ReadAllText(_jobsFile), _jsonOptions);
                if (list is not null)
                {
                    return list.ToDictionary(j => j.ItemId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read karaoke job list at {Path}; starting with an empty queue", _jobsFile);
        }

        return new Dictionary<Guid, KaraokeJob>();
    }

    private void Save()
    {
        var temp = _jobsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_jobs.Values.ToList(), _jsonOptions));
        File.Move(temp, _jobsFile, true);
    }
}
