#!/usr/bin/env python3
"""Karaoke separation worker.

Polls the Jellyfin Karaoke plugin for queued songs, splits each one into
instrumental and vocals, and uploads a single 4-channel Opus/WebM file:
channels 1-2 instrumental, channels 3-4 vocals.

Memory safety (built for a Raspberry Pi shared with Jellyfin):
  * Songs are separated in short chunks, so memory use does not grow with song length.
  * Separation runs in a child process that the kernel's out-of-memory killer
    will pick before anything else on the machine (oom_score_adj 1000).
  * The worker waits until enough memory is free before starting a song, and
    stops the child if it goes over MAX_MEMORY_MB or the machine runs short.

Settings (environment variables):
  JELLYFIN_URL       e.g. http://jellyfin.local:8096             (required)
  JELLYFIN_API_KEY   an API key from Dashboard > API Keys        (required)
  SEPARATOR          "demucs" (default) or "fake" (ffmpeg-only, for testing the plumbing)
  DEMUCS_MODEL       default "htdemucs"
  DEMUCS_SEGMENT     seconds the model looks at at once; lower uses less memory (default 4)
  CHUNK_SECONDS      length of each chunk of the song (default 20)
  THREADS            CPU threads for separation (default 2)
  MAX_MEMORY_MB      stop a song whose separation uses more than this (default 900). Never
                     allowed to exceed what is available at the start minus RESERVE_MB.
  RESERVE_MB         memory always left for everything else on the machine (default 500)
  MIN_FREE_MB        wait until this much memory is available before starting (default 1000)
  EMERGENCY_FREE_MB  stop separation if available memory falls below this (default 400)
  MAX_PRESSURE       stop separation if the kernel reports processes stalled waiting for
                     memory more than this % of the time (/proc/pressure/memory, default 10)
  POLL_SECONDS       wait between checks when idle (default 60)
  ACTIVE_HOURS       optional "start-end" in local 24h time, e.g. "23-7"; outside it the worker idles
  BITRATE            Opus bitrate for the 4-channel file (default 192k)
  RUN_ONCE           "1" = process at most one job and exit (for testing)

Test separation on a local file, without a server:
  python worker.py --separate INPUT_FILE OUTPUT_DIR
"""

import http.client
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

URL = os.environ.get("JELLYFIN_URL", "").rstrip("/")
KEY = os.environ.get("JELLYFIN_API_KEY", "")
SEPARATOR = os.environ.get("SEPARATOR", "demucs")
MODEL = os.environ.get("DEMUCS_MODEL", "htdemucs")
SEGMENT = float(os.environ.get("DEMUCS_SEGMENT", "4"))
CHUNK_SECONDS = float(os.environ.get("CHUNK_SECONDS", "20"))
OVERLAP_SECONDS = 1.0
THREADS = int(os.environ.get("THREADS", "2"))
MAX_MEMORY_MB = int(os.environ.get("MAX_MEMORY_MB", "900"))
MIN_FREE_MB = int(os.environ.get("MIN_FREE_MB", "1000"))
EMERGENCY_FREE_MB = int(os.environ.get("EMERGENCY_FREE_MB", "400"))
RESERVE_MB = int(os.environ.get("RESERVE_MB", "500"))
MAX_PRESSURE = float(os.environ.get("MAX_PRESSURE", "10"))
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "60"))
ACTIVE_HOURS = os.environ.get("ACTIVE_HOURS", "").strip()
BITRATE = os.environ.get("BITRATE", "192k")
RUN_ONCE = os.environ.get("RUN_ONCE", "") == "1"


def log(msg):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)


# ------------------------------------------------------------------ memory


IS_MAC = sys.platform == "darwin"


def mem_available_mb():
    if IS_MAC:
        # Free, inactive, speculative and purgeable pages can all be handed out without swapping.
        out = subprocess.run(["vm_stat"], stdout=subprocess.PIPE, text=True).stdout
        page = 4096
        pages = 0
        for line in out.splitlines():
            if "page size of" in line:
                page = int(line.split("page size of")[1].split()[0])
            name, _, value = line.partition(":")
            if name in ("Pages free", "Pages inactive", "Pages speculative", "Pages purgeable"):
                pages += int(value.strip().rstrip("."))
        return pages * page // (1024 * 1024)
    with open("/proc/meminfo") as f:
        for line in f:
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) // 1024
    return 0


def rss_mb(pid):
    if IS_MAC:
        out = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], stdout=subprocess.PIPE, text=True).stdout.strip()
        return int(out) // 1024 if out.isdigit() else 0
    try:
        with open("/proc/%d/status" % pid) as f:
            for line in f:
                if line.startswith("VmRSS:"):
                    return int(line.split()[1]) // 1024
    except OSError:
        pass
    return 0


def memory_pressure():
    """Percent of the last 10 s some process was stalled waiting for memory, or None if unknown.

    MemAvailable counts cached program code as free, so a machine can be thrashing
    while it still looks fine; stall time catches that."""
    try:
        with open("/proc/pressure/memory") as f:
            for line in f:
                if line.startswith("some"):
                    return float(line.split()[1].split("=")[1])
    except (OSError, IndexError, ValueError):
        pass
    return None


def wait_for_memory():
    """Blocks until MIN_FREE_MB is available. Returns False if it gave up (RUN_ONCE)."""
    warned = False
    while True:
        free = mem_available_mb()
        if free >= MIN_FREE_MB:
            return True
        if not warned:
            log("Waiting for memory: %d MB available, need %d MB" % (free, MIN_FREE_MB))
            warned = True
        if RUN_ONCE:
            return False
        time.sleep(POLL_SECONDS)


def run_guarded(args):
    """Runs this script as a child with the given args, watching its memory.

    Returns (ok, peak_mb, message)."""
    limit = min(MAX_MEMORY_MB, mem_available_mb() - RESERVE_MB)
    if limit < 300:
        return False, 0, "not enough free memory to start (%d MB available, %d MB kept in reserve)" % (
            mem_available_mb(), RESERVE_MB)

    proc = subprocess.Popen([sys.executable, "-u", os.path.abspath(__file__)] + args,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    lines = []
    reader = threading.Thread(target=lambda: lines.extend(proc.stdout), daemon=True)
    reader.start()

    peak = 0
    reason = None
    while proc.poll() is None:
        rss = rss_mb(proc.pid)
        peak = max(peak, rss)
        pressure = memory_pressure()
        if rss > limit:
            reason = "used more than its %d MB memory limit; try a lower DEMUCS_SEGMENT" % limit
        elif mem_available_mb() < EMERGENCY_FREE_MB:
            reason = "stopped because the machine was running low on memory"
        elif pressure is not None and pressure > MAX_PRESSURE:
            reason = "stopped because the machine was stalling for memory (%.0f%%)" % pressure
        if reason:
            proc.kill()
            break
        time.sleep(0.25)

    if reason:
        log("Stopping separation: %s (rss %d MB, available %d MB)" % (reason, peak, mem_available_mb()))
    proc.wait()
    reader.join(5)
    if reason:
        return False, peak, reason
    if proc.returncode == -9:
        return False, peak, "killed by the system for using too much memory"
    if proc.returncode != 0:
        tail = " ".join(l.strip() for l in lines[-4:])
        return False, peak, "separation failed: " + tail[-400:]
    return True, peak, ""


# --------------------------------------------------------- separation (child)


def child_separate(src, inst_path, voc_path):
    """Runs inside the guarded child process."""
    try:
        with open("/proc/self/oom_score_adj", "w") as f:
            f.write("1000")  # if memory runs out, the kernel kills this first
    except OSError:
        pass
    try:
        os.nice(10)
    except OSError:
        pass

    import numpy as np
    import soundfile as sf
    import torch
    from demucs.apply import apply_model
    from demucs.pretrained import get_model

    torch.set_num_threads(THREADS)
    model = get_model(MODEL)
    model.eval()
    sr = model.samplerate
    vocals = model.sources.index("vocals")

    # Decode to a float WAV on disk so the song is never held in memory whole.
    work = os.path.dirname(inst_path)
    decoded = os.path.join(work, "decoded.wav")
    run(["ffmpeg", "-v", "error", "-y", "-i", src, "-vn", "-ac", "2", "-ar", str(sr),
         "-c:a", "pcm_f32le", decoded])

    # Demucs normalises the whole song by its mono mix; do the same, streamed,
    # so every chunk is scaled identically.
    n = 0
    total_sum = 0.0
    total_sq = 0.0
    for block in sf.blocks(decoded, blocksize=sr * 10, dtype="float32", always_2d=True):
        mono = block.mean(axis=1, dtype=np.float64)
        n += mono.size
        total_sum += float(mono.sum())
        total_sq += float((mono * mono).sum())
    if n == 0:
        raise RuntimeError("the song has no audio")
    mean = total_sum / n
    std = max(math.sqrt(max(total_sq / n - mean * mean, 0.0)), 1e-8)

    chunk = int(CHUNK_SECONDS * sr)
    overlap = int(OVERLAP_SECONDS * sr)
    segment = min(SEGMENT, float(getattr(model, "segment", SEGMENT) or SEGMENT))

    with sf.SoundFile(inst_path, "w", sr, 2, subtype="FLOAT") as inst_out, \
            sf.SoundFile(voc_path, "w", sr, 2, subtype="FLOAT") as voc_out, \
            torch.inference_mode():
        tail = None
        start = 0
        while start < n:
            end = min(n, start + chunk)
            stop = min(n, end + overlap)
            block, _ = sf.read(decoded, start=start, stop=stop, dtype="float32", always_2d=True)
            mix = (torch.from_numpy(block.T.copy()) - mean) / std
            del block
            out = apply_model(model, mix[None], shifts=0, split=True, overlap=0.25,
                              segment=segment, progress=False)[0]
            del mix
            voc = out[vocals]
            inst = out.sum(0) - voc
            del out
            voc = (voc * std + mean).numpy().T
            inst = (inst * std + mean).numpy().T

            # Crossfade into the previous chunk's overlap so there is no click at the seam.
            if tail is not None:
                k = tail[0].shape[0]
                w = np.linspace(0.0, 1.0, k, dtype=np.float32)[:, None]
                inst[:k] = tail[0] * (1 - w) + inst[:k] * w
                voc[:k] = tail[1] * (1 - w) + voc[:k] * w

            keep = end - start
            tail = (inst[keep:].copy(), voc[keep:].copy()) if stop > end else None
            inst_out.write(inst[:keep])
            voc_out.write(voc[:keep])
            start = end

    os.remove(decoded)
    import resource
    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss  # bytes on macOS, KB on Linux
    print("peak_rss_mb=%d" % (peak // (1024 * 1024) if IS_MAC else peak // 1024), flush=True)


# --------------------------------------------------------------- pipeline


def request(method, path, data=None, timeout=120):
    req = urllib.request.Request(URL + path, data=data, method=method)
    req.add_header("Authorization", 'MediaBrowser Token="%s"' % KEY)
    # Proxies such as Cloudflare often reject Python's default "Python-urllib" agent.
    req.add_header("User-Agent", "JellyfinKaraokeWorker/1.0")
    if data is not None:
        req.add_header("Content-Type", "application/octet-stream")
    return urllib.request.urlopen(req, timeout=timeout)


def pick(obj, name):
    """Jellyfin may answer in PascalCase or camelCase depending on version."""
    if name in obj:
        return obj[name]
    return obj.get(name[0].lower() + name[1:])


def in_active_hours():
    if not ACTIVE_HOURS:
        return True
    start, end = (int(x) for x in ACTIVE_HOURS.split("-"))
    hour = time.localtime().tm_hour
    if start <= end:
        return start <= hour < end
    return hour >= start or hour < end


def claim():
    with request("POST", "/Karaoke/Jobs/Claim", data=b"") as resp:
        if resp.status == 204:
            return None
        return json.loads(resp.read().decode("utf-8"))


def download(item_id, dest, attempts=3):
    """Downloads the original song, retrying if the connection drops partway.

    A dropped connection can look like a normal end of file, and ffmpeg happily decodes a
    partial MP3, so a short download would otherwise become a short AI file.
    """
    for attempt in range(1, attempts + 1):
        try:
            with request("GET", "/Karaoke/Jobs/%s/Source" % item_id, timeout=600) as resp, \
                    open(dest, "wb") as f:
                expected = resp.headers.get("Content-Length")
                shutil.copyfileobj(resp, f)
                got = f.tell()
            if expected is not None and got != int(expected):
                raise IOError("download was cut off: got %d of %s bytes" % (got, expected))
            return
        except (OSError, http.client.HTTPException) as exc:
            if attempt == attempts:
                raise
            log("Download problem (%s); retrying in 30 s" % exc)
            time.sleep(30)


def audio_seconds(path):
    import soundfile as sf
    info = sf.info(path)
    return info.frames / float(info.samplerate)


def run(cmd):
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if proc.returncode != 0:
        tail = "\n".join(proc.stdout.strip().splitlines()[-6:])
        raise RuntimeError("%s failed: %s" % (os.path.basename(cmd[0]), tail))


def separate_fake(src, work):
    """No AI: treats a narrow band around 1 kHz as 'vocals'. Only for testing the pipeline."""
    inst = os.path.join(work, "no_vocals.wav")
    voc = os.path.join(work, "vocals.wav")
    run(["ffmpeg", "-v", "error", "-y", "-i", src, "-ac", "2",
         "-af", "bandreject=f=1000:width_type=h:w=400,bandreject=f=1000:width_type=h:w=400", inst])
    run(["ffmpeg", "-v", "error", "-y", "-i", src, "-ac", "2",
         "-af", "bandpass=f=1000:width_type=h:w=200,bandpass=f=1000:width_type=h:w=200", voc])
    return inst, voc


def separate(src, work):
    """Returns (instrumental_wav, vocals_wav, peak_mb)."""
    if SEPARATOR == "fake":
        inst, voc = separate_fake(src, work)
        return inst, voc, 0
    inst = os.path.join(work, "no_vocals.wav")
    voc = os.path.join(work, "vocals.wav")
    ok, peak, message = run_guarded(["--child-separate", src, inst, voc])
    if not ok:
        raise RuntimeError("%s (peak %d MB)" % (message, peak))
    return inst, voc, peak


def mux(inst, voc, dest):
    run([
        "ffmpeg", "-v", "error", "-y", "-i", inst, "-i", voc,
        "-filter_complex",
        "[0:a]aformat=channel_layouts=stereo[i];[1:a]aformat=channel_layouts=stereo[v];"
        "[i][v]join=inputs=2:channel_layout=quad:map=0.0-FL|0.1-FR|1.0-BL|1.1-BR[a]",
        "-map", "[a]", "-c:a", "libopus", "-b:a", BITRATE, "-mapping_family", "1",
        "-f", "webm", dest,
    ])


def process(job):
    item_id = pick(job, "ItemId")
    name = pick(job, "Name") or item_id
    ext = pick(job, "Extension") or ".audio"
    log("Separating: %s" % name)
    started = time.time()
    work = tempfile.mkdtemp(prefix="karaoke-")
    try:
        src = os.path.join(work, "input" + ext)
        download(item_id, src)
        inst, voc, peak = separate(src, work)
        # Never store an AI file that is shorter than the song: playback would stop where it ends.
        expected = pick(job, "RunTimeSeconds")
        if expected:
            got = audio_seconds(inst)
            if got < float(expected) - max(10.0, 0.1 * float(expected)):
                raise RuntimeError("the result is %.0f s long but the song is %.0f s; the download "
                                   "or decode was incomplete" % (got, float(expected)))
        stems = os.path.join(work, "stems.webm")
        mux(inst, voc, stems)
        with open(stems, "rb") as f:
            request("POST", "/Karaoke/Jobs/%s/Complete" % item_id, data=f.read(), timeout=600).close()
        log("Done: %s (%.1f min, peak %d MB)" % (name, (time.time() - started) / 60, peak))
    except Exception as exc:  # report every failure so the song does not sit in "processing"
        log("Failed: %s: %s" % (name, exc))
        try:
            request("POST", "/Karaoke/Jobs/%s/Fail" % item_id, data=str(exc)[:500].encode("utf-8")).close()
        except Exception as report_exc:
            log("Could not report the failure: %s" % report_exc)
    finally:
        shutil.rmtree(work, ignore_errors=True)


def test_separate(src, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    log("Test separation of %s (segment %.1fs, chunk %.0fs, %d threads, limit %d MB, %d MB available)"
        % (src, SEGMENT, CHUNK_SECONDS, THREADS, MAX_MEMORY_MB, mem_available_mb()))
    started = time.time()
    inst, voc, peak = separate(src, out_dir)
    stems = os.path.join(out_dir, "stems.webm")
    mux(inst, voc, stems)
    log("OK in %.1f min, peak %d MB -> %s" % ((time.time() - started) / 60, peak, out_dir))


def main():
    if len(sys.argv) == 5 and sys.argv[1] == "--child-separate":
        child_separate(sys.argv[2], sys.argv[3], sys.argv[4])
        return
    if len(sys.argv) == 4 and sys.argv[1] == "--separate":
        test_separate(sys.argv[2], sys.argv[3])
        return

    if not URL or not KEY:
        sys.exit("JELLYFIN_URL and JELLYFIN_API_KEY must both be set.")
    log("Karaoke worker started (server %s, separator %s, memory limit %d MB)" % (URL, SEPARATOR, MAX_MEMORY_MB))

    while True:
        job = None
        if in_active_hours() and (SEPARATOR == "fake" or wait_for_memory()):
            try:
                job = claim()
            except (urllib.error.URLError, OSError) as exc:
                log("Cannot reach Jellyfin: %s" % exc)
        if job:
            process(job)
        if RUN_ONCE:
            return
        if not job:
            time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
