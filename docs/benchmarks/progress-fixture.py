from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import time

SIZE = 32 * 1024 * 1024
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        start = 0
        end = SIZE - 1
        value = self.headers.get('Range', '')
        if value.startswith('bytes='):
            bounds = value[6:].split('-', 1)
            start = int(bounds[0])
            if bounds[1]: end = min(end, int(bounds[1]))
        self.send_response(206 if value else 200)
        self.send_header('Content-Type', 'application/octet-stream')
        self.send_header('Accept-Ranges', 'bytes')
        self.send_header('Content-Length', str(end - start + 1))
        if value: self.send_header('Content-Range', f'bytes {start}-{end}/{SIZE}')
        self.end_headers()
        try:
            for offset in range(start, end + 1, 16384):
                self.wfile.write(b'Z' * min(16384, end - offset + 1))
                self.wfile.flush()
                time.sleep(0.125)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
    def log_message(self, *_): pass

server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
Path(__file__).with_name('progress-port.txt').write_text(str(server.server_port))
server.serve_forever()
