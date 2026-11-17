#!/bin/sh
# Runs the karaoke separation worker on this Mac.
# Settings come from worker/.env (copy .env.example and fill in the API key).
cd "$(dirname "$0")" || exit 1
# Login items start with a minimal PATH; ffmpeg lives in /usr/local/bin (Intel Homebrew) or /opt/homebrew/bin.
PATH="/usr/local/bin:/opt/homebrew/bin:$PATH"
export PATH
if [ ! -f .env ]; then
    echo "Missing worker/.env - copy .env.example to .env and add your Jellyfin API key." >&2
    exit 1
fi
set -a
. ./.env
set +a
exec nice -n 10 .venv/bin/python -u worker.py
