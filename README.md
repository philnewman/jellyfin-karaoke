# Jellyfin Karaoke

Karaoke mode for the Jellyfin web client. A microphone button in the now-playing bar opens a
**Karaoke mode** switch and a **Vocals** slider (0–100%), so you can sing along over the music
from your own library.

Two ways of lowering the vocals, picked automatically for each song:

| | Instant mode | AI mode |
|---|---|---|
| Available | Any song, immediately | Songs that have been processed |
| How it works | The browser turns down whatever sits in the centre of the stereo mix, where the lead vocal usually is. Bass is kept. | [Demucs](https://github.com/facebookresearch/demucs), a neural network, splits the song into vocals and instrumental ahead of time. The slider sets how much of the vocal is mixed back in. |
| Quality | Rough: snare and other centred instruments drop too, and vocal reverb often remains | Clean: drums, guitars and the stereo image are untouched |
| Cost | None | About 2 minutes of CPU per minute of music, about 1.1 GB of memory while working, about 1.4 MB of storage per minute of music |

With karaoke mode on, a song that has no AI version plays in instant mode and is queued for
processing. The next time it plays, the AI version is used. If it becomes ready while the song is
playing, the panel offers to switch over without losing your place.

Built for **Jellyfin 12.2** and the web client in Chrome, Edge or other Chromium browsers.

**Pairs with [Jellyfin Karaoke Lyrics](https://github.com/philnewman/jellyfin-karaoke-lyrics)**,
which highlights the current line of synchronized lyrics and keeps it scrolled into view. This
plugin lowers the vocals; that one shows the words. The two can be installed side by side.

## Contents

- [How the pieces fit](#how-the-pieces-fit)
- [1. Install the server plugin](#1-install-the-server-plugin)
- [2. Check the karaoke button](#2-check-the-karaoke-button)
- [3. Set up the AI worker](#3-set-up-the-ai-worker)
- [Using it](#using-it)
- [Settings](#settings)
- [Troubleshooting](#troubleshooting)
- [Limits](#limits)
- [Development](#development)

## How the pieces fit

```
 Browser (Jellyfin web client)         Jellyfin server + Karaoke plugin           AI worker (any computer)
 ┌───────────────────────────┐         ┌──────────────────────────────┐          ┌──────────────────────────┐
 │ karaoke.js                │  plays  │ adds karaoke.js to the page  │  claims  │ worker.py                │
 │ - mic button, slider      │ ◀────── │ keeps the queue of songs     │ ◀─────── │ - downloads the song     │
 │ - instant mode (Web Audio)│         │ stores and serves AI versions│ ───────▶ │ - runs Demucs            │
 │ - queues songs for AI     │ ──────▶ │                              │ uploads  │ - uploads the result     │
 └───────────────────────────┘         └──────────────────────────────┘          └──────────────────────────┘
```

- **Server plugin** (`Jellyfin.Plugin.Karaoke/`): adds the client script to the web client as
  `index.html` is served (nothing on disk is changed), keeps the queue, and stores each finished
  song as one 4-channel Opus file (instrumental on channels 1–2, vocals on 3–4) under
  `<jellyfin data>/karaoke/stems/`.
- **Client script** (`Jellyfin.Plugin.Karaoke/Web/karaoke.js`): hooks the web client's own audio
  player, so play, pause, seek, the queue and progress reporting all work as usual.
- **Worker** (`worker/`): does the separation. It only needs the server's address and an API key,
  so it can run on any machine on your network, and should run on one with memory to spare.

Instant mode needs only steps 1 and 2. AI mode also needs step 3.

## 1. Install the server plugin

### From the plugin catalog (recommended)

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (on older versions, the
   **Repositories** tab under Plugins) and add a repository:
   - Name: `Karaoke`
   - URL: `https://raw.githubusercontent.com/philnewman/jellyfin-karaoke/main/manifest.json`
2. Open **Catalog**, find **Karaoke** under General, and install it.
3. Restart Jellyfin.

Installing from the catalog fills in the plugin's developer and repository details and lets
Jellyfin offer updates. To switch a copy you installed by hand, delete its `plugins/Karaoke`
folder, then install from the catalog and restart.

### Or install it by hand

#### Get the plugin file

Either download `jellyfin-plugin-karaoke.zip` from this repository's **Releases** page and unzip it,
or build it yourself. Building needs the .NET 10 SDK, or just Docker:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish Jellyfin.Plugin.Karaoke -c Release -o out
```

```bash
dotnet publish Jellyfin.Plugin.Karaoke -c Release -o out
```

Either way you need one file: `Jellyfin.Plugin.Karaoke.dll`.

#### Copy it into Jellyfin

1. In Jellyfin's config folder (`/config` inside the official Docker image), create
   `plugins/Karaoke/`.
2. Copy `Jellyfin.Plugin.Karaoke.dll` into it.
3. Restart Jellyfin. With Docker:

   ```bash
   docker restart jellyfin
   ```

4. Check **Dashboard → Plugins**: "Karaoke" should be listed as Active. The server log
   shows `Loaded plugin: Karaoke 1.1.0.0`. A hand-installed copy shows Developer and Repository as
   "Unknown"; that's only cosmetic.

## 2. Check the karaoke button

1. Reload the Jellyfin tab in your browser (Cmd+Shift+R / Ctrl+Shift+R).
2. Play any song. A microphone button appears in the now-playing bar, next to the volume control.
3. Click it, tick **Karaoke mode**, and drag **Vocals** to 0%. The lead vocal should drop sharply
   while bass and drums stay.

That is instant mode working. The panel says "This song is queued for AI separation" because
karaoke mode queues each new song; nothing processes it until the worker is running.

**Using the JavaScript Injector plugin instead.** If you already use
[JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) and prefer it to
add scripts, untick "Add the karaoke button to the web client" in the Karaoke plugin's settings
and add a script with this content:

```js
document.head.appendChild(Object.assign(document.createElement('script'), { src: '/Karaoke/ClientScript' }));
```

If both are on, the script only runs once, so nothing breaks.

## 3. Set up the AI worker

### Pick a machine

Separation peaks at about **1.1 GB of memory** and keeps several CPU cores busy. Run the worker on
a computer that has that to spare: a desktop or laptop, or a server with at least 2 GB free.

**Do not run it on a Raspberry Pi that is also running Jellyfin.** On a 4 GB Pi 4 with the usual
containers there is only about 450 MB to spare. A test there stalled the whole machine until it
was rebooted.

The worker processes songs only while its machine is on and awake. Queued songs wait until then.

### On a Mac

Needs [Homebrew](https://brew.sh) for `uv` and `ffmpeg`:

```bash
brew install uv ffmpeg
```

1. In Jellyfin, go to **Dashboard → API Keys → New API Key**, name it "Karaoke worker", and copy
   the key.
2. Run the installer. The first run creates `worker/.env` and stops:

   ```bash
   ./worker/install-mac.sh
   ```

3. Edit `worker/.env`: set `JELLYFIN_URL` and paste the key into `JELLYFIN_API_KEY`.
   See [Mac and local addresses](#mac-and-local-addresses) before choosing the URL.
4. Run the installer again. It downloads Demucs and PyTorch (about 1 GB), then starts the worker
   and sets it to start at every login:

   ```bash
   ./worker/install-mac.sh
   ```

The worker logs to `~/Library/Logs/karaoke-worker.log`. Watch it:

```bash
tail -f ~/Library/Logs/karaoke-worker.log
```

Stop it and remove the login item (the worker folder and `.env` are kept):

```bash
./worker/install-mac.sh uninstall
```

PyTorch 2.2.2 is pinned because it is the last release for Intel Macs; it works on Apple Silicon too.

### On Linux, in Docker

1. Create an API key as in step 1 above.
2. Put the key and your server's address in `worker/docker-compose.example.yml`.
3. Start it:

   ```bash
   docker compose -f worker/docker-compose.example.yml up -d --build
   ```

4. Watch it:

   ```bash
   docker logs -f karaoke-worker
   ```

The first song downloads the Demucs model (about 80 MB) into the `karaoke-models` volume.

### Anywhere else, by hand

Python 3.11, ffmpeg with libopus, and:

```bash
pip install "torch==2.2.2" "torchaudio==2.2.2" "numpy<2" "demucs==4.0.1" soundfile
```

```bash
JELLYFIN_URL=http://your-server:8096 JELLYFIN_API_KEY=your-key python worker/worker.py
```

### Check it works

Play a song with karaoke mode on, then within a minute the worker log shows
`Separating: <song>`, and later `Done: <song> (… min, peak … MB)`. Play the song again with
karaoke mode on: the panel says "Using AI-separated vocals for this song (best quality)".

## Using it

- **Karaoke mode** switches vocal removal on and off for the web client in this browser.
- **Vocals** sets how much voice is left: 0% for karaoke, around 30% for a quiet guide vocal,
  100% for the original. The setting is remembered in this browser.
- Songs are queued only when you turn karaoke mode on while they play, or when you queue them
  yourself. Ordinary playback never queues anything, and other Jellyfin apps (TV, phone) are not
  affected.
- **Preparing ahead.** To process a whole album or playlist before a karaoke night, open
  **Dashboard → Plugins → Karaoke → Settings**, paste its item ID into "Queue a song, album or
  playlist", and click **Queue**. The ID is the part of the item's page address after `id=`.
  Allow roughly twice the music's length: a 20-song playlist takes about 3 hours.
- **Progress.** The "Separation queue" at the bottom of the same settings page lists waiting,
  processing and failed songs. Reload the page to update it. A song leaves the list when it is ready.
- **Karaoke Ready playlist (optional).** Tick "Keep a playlist of every song with AI karaoke
  stems" in the settings and save. The plugin creates a public playlist (named "Karaoke Ready"
  unless you change it) holding every song with an AI version, sorted by artist, album and track,
  and adds each new song as the worker finishes it. Turning the option off leaves the playlist as
  it is; delete it yourself if you no longer want it.
- **Whole library?** Possible but rarely worth it: 10,000 songs is several weeks of processing and
  around 60–70 GB of storage.

## Settings

### Plugin (Dashboard → Plugins → Karaoke → Settings)

| Setting | Default | Meaning |
|---|---|---|
| Add the karaoke button to the web client | on | Adds the client script to the web client. Turn off if JavaScript Injector loads it instead. |
| Queue a song for AI separation the first time karaoke mode is used on it | on | Turn off to queue only by hand. |
| Worker timeout (minutes) | 240 | A song a worker has held longer than this goes back in the queue (up to 3 attempts). |
| Keep a playlist of every song with AI karaoke stems | off | Maintains a public playlist of ready songs. |
| Playlist name | Karaoke Ready | Renaming starts a new playlist; the old one stays. |
| Playlist owner | First administrator | The user who can edit the playlist. |

### Worker (environment variables, or `worker/.env` on a Mac)

| Variable | Default | Meaning |
|---|---|---|
| `JELLYFIN_URL` | (required) | Server address as the worker's machine reaches it |
| `JELLYFIN_API_KEY` | (required) | From Dashboard → API Keys |
| `THREADS` | 2 | CPU threads for separation |
| `MAX_MEMORY_MB` | 900 | Stop a song whose separation uses more. Never above free memory at the start minus `RESERVE_MB`. |
| `RESERVE_MB` | 500 | Memory always left for everything else |
| `MIN_FREE_MB` | 1000 | Wait until this much is free before starting a song |
| `EMERGENCY_FREE_MB` | 400 | Stop separation if free memory falls below this |
| `MAX_PRESSURE` | 10 | Stop separation if processes stall waiting for memory more than this % of the time (Linux with `/proc/pressure`) |
| `DEMUCS_SEGMENT` | 4 | Seconds the model looks at at once; lower uses a little less memory |
| `CHUNK_SECONDS` | 20 | The song is processed in chunks of this length, so memory does not grow with song length |
| `DEMUCS_MODEL` | htdemucs | Demucs model name |
| `ACTIVE_HOURS` | (always) | e.g. `23-7` to process only overnight |
| `POLL_SECONDS` | 60 | How often an idle worker checks the queue |
| `BITRATE` | 192k | Opus bitrate of the stored 4-channel file |

To try separation on a file without a server:

```bash
python worker/worker.py --separate song.mp3 out-folder
```

## Troubleshooting

**No microphone button.** Reload the tab with Cmd+Shift+R / Ctrl+Shift+R. Check the plugin is
Active under Dashboard → Plugins and that its "Add the karaoke button" setting is on (or that your
JavaScript Injector script is enabled). The button only appears once a song is playing.

**Vocals barely change in instant mode.** The vocal is not centred in that recording, or it is
mono. Wait for the AI version.

**404 errors for `/Karaoke/...` in the browser console.** The server plugin is not installed or
not loaded. Instant mode still works.

<a id="mac-and-local-addresses"></a>
**Mac: worker log says `No route to host` for a local address.** macOS blocks background programs
from reaching devices on your home network until they are allowed under **System Settings →
Privacy & Security → Local Network**. Allow Python there, or use the server's public `https`
address in `worker/.env`.

**Worker log says `HTTP Error 403`.** A proxy in front of Jellyfin (Cloudflare, for example) is
rejecting the request. The worker identifies itself as `JellyfinKaraokeWorker/1.0`; allow that
user agent, or use the server's local address.

**Worker log says `No such file or directory: 'ffmpeg'`.** The worker cannot find ffmpeg.
`run-mac.sh` adds `/usr/local/bin` and `/opt/homebrew/bin`; elsewhere, put ffmpeg on the `PATH`.

**Worker log says it used more than its memory limit, or is waiting for memory.** Close other
programs, raise `MAX_MEMORY_MB` if the machine has room, or lower `DEMUCS_SEGMENT`.
Failed songs can be queued again from the settings page.

**A song is stuck as "Processing".** The worker stopped mid-song. After the worker timeout it goes
back in the queue automatically.

## Limits

- Web client only, in Chromium-based browsers. Firefox and Safari have not been tested with the
  4-channel AI files.
- Music played through the audio player only, not music videos.
- Songs the server transcodes to HLS for the browser get instant mode only.
- If audio normalisation is off in the web client and karaoke mode is switched on first,
  normalisation stays inactive until the tab is reloaded.

## Development

```
Jellyfin.Plugin.Karaoke/
  Api/KaraokeController.cs       HTTP endpoints under /Karaoke
  Services/StemStore.cs          queue (jobs.json) and stored AI files
  Services/PlaylistSync.cs       optional Karaoke Ready playlist
  Web/karaoke.js                 client script
  Web/ScriptInjectionStartupFilter.cs   adds the script tag to index.html
  Configuration/                 plugin settings and settings page
worker/
  worker.py                      separation worker
  install-mac.sh, run-mac.sh, karaoke-worker.plist.template
  Dockerfile, docker-compose.example.yml
test/
  mock_server.py, player.html    stand-ins for Jellyfin and its web player
manifest.json                    Jellyfin plugin repository (updated by CI on each release)
scripts/update_manifest.py       adds a release to manifest.json
```

### Endpoints

| Method and path | Who | Purpose |
|---|---|---|
| `GET /Karaoke/ClientScript` | anyone | The client script |
| `GET /Karaoke/State` | signed-in user | IDs of songs with AI versions |
| `GET /Karaoke/Items/{id}/Status` | signed-in user | `none`, `queued`, `processing`, `failed` or `ready` |
| `POST /Karaoke/Items/{id}/Queue` | signed-in user | Queue a song, or every song in an album, playlist or folder |
| `GET /Karaoke/Items/{id}/Stems` | signed-in user | The 4-channel AI file |
| `DELETE /Karaoke/Items/{id}/Stems` | admin | Remove a song's AI file |
| `GET /Karaoke/Queue` | admin | Queue contents |
| `POST /Karaoke/Playlist/Sync` | admin | Bring the Karaoke Ready playlist up to date now |
| `POST /Karaoke/Jobs/Claim` | admin / API key | Worker takes the next song |
| `GET /Karaoke/Jobs/{id}/Source` | admin / API key | Worker downloads the original file |
| `POST /Karaoke/Jobs/{id}/Complete` | admin / API key | Worker uploads the result |
| `POST /Karaoke/Jobs/{id}/Fail` | admin / API key | Worker reports a failure |

### Testing without a server

```bash
python3 test/mock_server.py 8766
```

Then open `http://127.0.0.1:8766/web/index.html` (add `?norm=0` to imitate audio normalisation
being off). `measure()` in the browser console reports the level of each test tone. The mock needs a
test song at `test/data/song.flac` with 60 Hz and 1000 Hz in the centre, 440 Hz left only and
2500 Hz right only:

```bash
mkdir -p test/data && ffmpeg -f lavfi -i sine=f=1000:d=30 -f lavfi -i sine=f=60:d=30 -f lavfi -i sine=f=440:d=30 -f lavfi -i sine=f=2500:d=30 -filter_complex '[0][1]amix=inputs=2:normalize=0,asplit[c1][c2];[c1][2]amix=inputs=2:normalize=0[l];[c2][3]amix=inputs=2:normalize=0[r];[l][r]join=inputs=2:channel_layout=stereo[a]' -map '[a]' test/data/song.flac
```

Run the worker against the mock with an ffmpeg-only stand-in for Demucs:

```bash
JELLYFIN_URL=http://127.0.0.1:8766 JELLYFIN_API_KEY=testtoken SEPARATOR=fake RUN_ONCE=1 python3 worker/worker.py
```

## License

GPL-3.0, because the plugin builds on Jellyfin's GPL-3.0 libraries. See [LICENSE](LICENSE).
Demucs is MIT-licensed and is downloaded by the worker, not included here.
