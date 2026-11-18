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
# WORKER_SPEED=background (default) lets macOS throttle the worker so it never slows down
# what you're doing: about 16 minutes for a 4-minute song. normal takes about 9.
case "${WORKER_SPEED:-background}" in
    background) exec /usr/sbin/taskpolicy -b nice -n 10 .venv/bin/python -u worker.py ;;
    normal) exec nice -n 10 .venv/bin/python -u worker.py ;;
    *) echo "WORKER_SPEED must be background or normal, not '$WORKER_SPEED'." >&2; exit 1 ;;
esac
