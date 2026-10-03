#!/usr/bin/env python3
"""Local synthetic login/download fixture. No real account or browser data is read."""
import argparse
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import secrets
import threading
import time
from urllib.parse import parse_qs, urlsplit
import zipfile

COOKIE = "colibri_fixture_session"
ZIP_SIZE = 32 * 1024 * 1024


def make_payload():
    # A stored ZIP has a 120-byte header/directory overhead for this filename.
    content_size = ZIP_SIZE - 120
    pattern = b"Colibri synthetic cookie fixture.\n"
    content = (pattern * ((content_size // len(pattern)) + 1))[:content_size]
    output = io.BytesIO()
    entry = zipfile.ZipInfo("fixture.bin", date_time=(1980, 1, 1, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_STORED
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr(entry, content)
    payload = output.getvalue()
    if len(payload) != ZIP_SIZE:
        raise ValueError("Unexpected ZIP size")
    return payload


def byte_range(value, size):
    if value is None:
        return 0, size - 1, 200
    if not value.startswith("bytes=") or "," in value:
        raise ValueError("Only one byte range is supported")
    start, separator, end = value[6:].partition("-")
    if not separator or (not start and not end):
        raise ValueError("Invalid range")
    if not start:
        suffix = int(end)
        if suffix <= 0:
            raise ValueError("Invalid suffix")
        return max(0, size - suffix), size - 1, 206
    first = int(start)
    last = min(int(end), size - 1) if end else size - 1
    if first < 0 or first >= size or last < first:
        raise ValueError("Unsatisfiable range")
    return first, last, 206


class FixtureServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, chunk_delay=0.01):
        if address[0] not in ("127.0.0.1", "::1"):
            raise ValueError("Only a loopback address is permitted")
        self.payload = make_payload()
        self.chunk_delay = chunk_delay
        self.sessions = set()
        self.counts = {}
        self.gate = threading.Lock()
        super().__init__(address, Handler)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass  # Never print URL/query, supplied login fields, cookies or headers.

    def authorized(self):
        try:
            cookies = SimpleCookie(self.headers.get("Cookie", ""))
            token = cookies[COOKIE].value if COOKIE in cookies else None
        except Exception:
            return False
        with self.server.gate:
            return token in self.server.sessions

    def response(self, status, case, body=b"", headers=None):
        authenticated = self.authorized()
        with self.server.gate:
            key = (self.command, case, authenticated, status)
            self.server.counts[key] = self.server.counts.get(key, 0) + 1
            count = self.server.counts[key]
        print(f"case={case} method={self.command} authenticated={authenticated} status={status} count={count}", flush=True)
        self.send_response(status)
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if self.command != "HEAD" and body:
            try:
                for offset in range(0, len(body), 64 * 1024):
                    self.wfile.write(body[offset:offset + 64 * 1024])
                    if case in ("protected-download", "public-download") and self.server.chunk_delay:
                        time.sleep(self.server.chunk_delay)
            except (BrokenPipeError, ConnectionResetError):
                pass  # Browser cancellation during handoff is expected.

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/":
            body = b'''<!doctype html><html lang="en"><meta charset="utf-8"><title>Colibri cookie fixture</title>
<h1>Local cookie fixture</h1><p>Synthetic login: admin / password</p>
<form action="/login" method="post"><label>Username <input name="username" autocomplete="off"></label>
<label>Password <input name="password" type="password" autocomplete="off"></label><button>Sign in</button></form>
<p><a href="/protected/test.zip">Authenticated test ZIP</a> | <a href="/public/test.zip">Public comparison ZIP</a> | <a href="/logout">Sign out</a></p></html>'''
            self.response(200, "home", body, {"Content-Type": "text/html; charset=utf-8"})
        elif path in ("/protected/test.zip", "/public/test.zip"):
            protected = path.startswith("/protected/")
            case = "protected-download" if protected else "public-download"
            if protected and not self.authorized():
                self.response(401, case, b"Sign in at the local fixture page first.")
                return
            try:
                first, last, status = byte_range(self.headers.get("Range"), len(self.server.payload))
            except (ValueError, OverflowError):
                self.response(416, case, headers={"Content-Range": f"bytes */{len(self.server.payload)}"})
                return
            headers = {"Content-Type": "application/zip", "Content-Disposition": 'attachment; filename="test.zip"', "Accept-Ranges": "bytes"}
            if status == 206:
                headers["Content-Range"] = f"bytes {first}-{last}/{len(self.server.payload)}"
            self.response(status, case, self.server.payload[first:last + 1], headers)
        elif path == "/logout":
            try:
                cookie = SimpleCookie(self.headers.get("Cookie", ""))
                token = cookie[COOKIE].value if COOKIE in cookie else None
            except Exception:
                token = None
            with self.server.gate:
                self.server.sessions.discard(token)
            self.response(303, "logout", headers={"Location": "/", "Set-Cookie": f"{COOKIE}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax"})
        else:
            self.response(404, "not-found", b"Not found")

    def do_POST(self):
        if urlsplit(self.path).path != "/login":
            self.response(404, "not-found")
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 0 or length > 8192:
                self.response(413, "login")
                return
            fields = parse_qs(self.rfile.read(length).decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            self.response(400, "login")
            return
        if fields.get("username") != ["admin"] or fields.get("password") != ["password"]:
            self.response(401, "login", b"Invalid synthetic login")
            return
        token = secrets.token_urlsafe(32)
        with self.server.gate:
            self.server.sessions.add(token)
        self.response(303, "login", headers={"Location": "/", "Set-Cookie": f"{COOKIE}={token}; Path=/; HttpOnly; SameSite=Lax"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--chunk-delay", type=float, default=0.01)
    args = parser.parse_args()
    if not 0 <= args.port <= 65535 or not 0 <= args.chunk_delay <= 1:
        parser.error("Port must be0..65535 and chunk delay0..1 seconds")
    server = FixtureServer(("127.0.0.1", args.port), args.chunk_delay)
    print(f"Local fixture: http://127.0.0.1:{server.server_port}/", flush=True)
    print("Synthetic username: admin; synthetic password: password", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
