#!/usr/bin/env python3
"""Stand-in for Jellyfin + the Karaoke plugin, for testing karaoke.js and worker.py
without a real server. Mirrors the plugin's HTTP API (PascalCase JSON, token required).

  python3 test/mock_server.py [port]      then open http://127.0.0.1:<port>/web/index.html
"""

import json
import os
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SCRIPT = os.path.join(ROOT, "Jellyfin.Plugin.Karaoke", "Web", "karaoke.js")
DATA = os.path.join(HERE, "data")
STEMS = os.path.join(DATA, "stems")
SONG = os.path.join(DATA, "song.flac")
TOKEN = "testtoken"

jobs = {}  # item id -> {"Name", "State", "Error", "QueuedAt"}


def stem_path(item_id):
    return os.path.join(STEMS, item_id + ".webm")


def state_of(item_id):
    if os.path.exists(stem_path(item_id)):
        return "ready", None
    job = jobs.get(item_id)
    return (job["State"], job["Error"]) if job else ("none", None)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.command, self.path))

    def authed(self, query):
        header = self.headers.get("Authorization", "")
        return ('Token="%s"' % TOKEN) in header or query.get("ApiKey", [""])[0] == TOKEN

    def send_json(self, obj, status=200):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_empty(self, status=204):
        self.send_response(status)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def send_file(self, path, ctype):
        if not os.path.exists(path):
            return self.send_empty(404)
        size = os.path.getsize(path)
        start, end = 0, size - 1
        m = re.match(r"bytes=(\d*)-(\d*)", self.headers.get("Range", ""))
        if m and (m.group(1) or m.group(2)):
            if m.group(1):
                start = int(m.group(1))
                if m.group(2):
                    end = min(int(m.group(2)), size - 1)
            else:
                start = max(0, size - int(m.group(2)))
            self.send_response(206)
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        else:
            self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        with open(path, "rb") as f:
            f.seek(start)
            try:
                self.wfile.write(f.read(end - start + 1))
            except (BrokenPipeError, ConnectionResetError):
                pass

    def body(self):
        return self.rfile.read(int(self.headers.get("Content-Length", "0") or 0))

    def do_GET(self):
        url = urlparse(self.path)
        query = parse_qs(url.query)
        path = url.path

        if path in ("/web/", "/web/index.html"):
            return self.send_file(os.path.join(HERE, "player.html"), "text/html")
        if path == "/Karaoke/ClientScript":
            return self.send_file(SCRIPT, "application/javascript")
        if not self.authed(query):
            return self.send_empty(401)

        if path == "/Karaoke/State":
            ids = [f[:-5] for f in os.listdir(STEMS) if f.endswith(".webm")]
            return self.send_json({"AutoQueue": True, "Ids": ids})
        m = re.match(r"^/Karaoke/Items/([0-9a-f]+)/Status$", path)
        if m:
            state, error = state_of(m.group(1))
            return self.send_json({"State": state, "Error": error})
        m = re.match(r"^/Karaoke/Items/([0-9a-f]+)/Stems$", path)
        if m:
            return self.send_file(stem_path(m.group(1)), "audio/webm")
        if re.match(r"^/Karaoke/Jobs/[0-9a-f]+/Source$", path) or re.match(r"^/Audio/[0-9a-f-]+/", path):
            return self.send_file(SONG, "audio/flac")
        self.send_empty(404)

    def do_POST(self):
        url = urlparse(self.path)
        path = url.path
        data = self.body()
        if not self.authed(parse_qs(url.query)):
            return self.send_empty(401)

        m = re.match(r"^/Karaoke/Items/([0-9a-f]+)/Queue$", path)
        if m:
            item_id = m.group(1)
            queued = 0
            state, _ = state_of(item_id)
            if state in ("none", "failed"):
                jobs[item_id] = {"Name": "Test song " + item_id[:4], "State": "queued", "Error": None, "QueuedAt": time.time()}
                queued = 1
            return self.send_json({"Queued": queued, "State": state_of(item_id)[0]})
        if path == "/Karaoke/Jobs/Claim":
            waiting = sorted((j["QueuedAt"], i) for i, j in jobs.items() if j["State"] == "queued")
            if not waiting:
                return self.send_empty(204)
            item_id = waiting[0][1]
            jobs[item_id]["State"] = "processing"
            return self.send_json({"ItemId": item_id, "Name": jobs[item_id]["Name"], "Extension": ".flac"})
        m = re.match(r"^/Karaoke/Jobs/([0-9a-f]+)/Complete$", path)
        if m:
            with open(stem_path(m.group(1)), "wb") as f:
                f.write(data)
            jobs.pop(m.group(1), None)
            return self.send_empty(204)
        m = re.match(r"^/Karaoke/Jobs/([0-9a-f]+)/Fail$", path)
        if m:
            if m.group(1) in jobs:
                jobs[m.group(1)].update(State="failed", Error=data.decode("utf-8", "replace"))
            return self.send_empty(204)
        self.send_empty(404)


if __name__ == "__main__":
    os.makedirs(STEMS, exist_ok=True)
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8766
    print("Mock Jellyfin on http://127.0.0.1:%d/web/index.html" % port, flush=True)
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
