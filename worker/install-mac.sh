#!/bin/sh
# Sets up the karaoke separation worker on a Mac and starts it at every login.
#
#   ./worker/install-mac.sh             install (or update) and start
#   ./worker/install-mac.sh uninstall   stop and remove the login item
#
# Needs: uv (https://docs.astral.sh/uv/) and ffmpeg with libopus (brew install uv ffmpeg).
set -eu

WORKER_DIR="$(cd "$(dirname "$0")" && pwd)"
LABEL="local.karaoke-worker"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG="$HOME/Library/Logs/karaoke-worker.log"
DOMAIN="gui/$(id -u)"

if [ "${1:-}" = "uninstall" ]; then
    launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
    rm -f "$PLIST"
    echo "Removed the login item. The worker folder and its .env are untouched."
    exit 0
fi

PATH="/usr/local/bin:/opt/homebrew/bin:$PATH"
for tool in uv ffmpeg; do
    command -v "$tool" >/dev/null || { echo "Missing $tool. Install it with: brew install $tool" >&2; exit 1; }
done
ffmpeg -hide_banner -encoders 2>/dev/null | grep -q libopus || {
    echo "Your ffmpeg has no Opus encoder (libopus). Install Homebrew's ffmpeg." >&2; exit 1; }

# PyTorch dropped Intel Macs after 2.2.2; Apple Silicon can use newer releases, but 2.2.2
# works on both and Demucs 4.0.1 is tested with it.
if [ ! -x "$WORKER_DIR/.venv/bin/python" ]; then
    echo "Creating the Python environment (downloads about 1 GB)..."
    uv venv -q -p 3.11 "$WORKER_DIR/.venv"
fi
uv pip install -q -p "$WORKER_DIR/.venv/bin/python" \
    "torch==2.2.2" "torchaudio==2.2.2" "numpy<2" "demucs==4.0.1" soundfile

if [ ! -f "$WORKER_DIR/.env" ]; then
    cp "$WORKER_DIR/.env.example" "$WORKER_DIR/.env"
    chmod 600 "$WORKER_DIR/.env"
    echo
    echo "Created $WORKER_DIR/.env"
    echo "Fill in JELLYFIN_URL and JELLYFIN_API_KEY there, then run this script again."
    exit 0
fi
if grep -q "paste-your-api-key-here" "$WORKER_DIR/.env"; then
    echo "Set JELLYFIN_API_KEY in $WORKER_DIR/.env first." >&2
    exit 1
fi
chmod +x "$WORKER_DIR/run-mac.sh"

mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
sed -e "s#__RUN_SCRIPT__#$WORKER_DIR/run-mac.sh#" -e "s#__LOG__#$LOG#" \
    "$WORKER_DIR/karaoke-worker.plist.template" > "$PLIST"
plutil -lint -s "$PLIST"

launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
launchctl bootstrap "$DOMAIN" "$PLIST"

echo "The worker is running and will start at every login."
echo "Log: $LOG"
echo "Watch it with: tail -f \"$LOG\""
