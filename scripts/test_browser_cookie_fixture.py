import contextlib
import http.client
import importlib.util
import io
from pathlib import Path
import threading
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("cookie_fixture", Path(__file__).with_name("browser-cookie-fixture.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.console = contextlib.redirect_stdout(io.StringIO())
        cls.console.__enter__()
        cls.server = fixture.FixtureServer(("127.0.0.1", 0), chunk_delay=0)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        cls.console.__exit__(None, None, None)

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.request(method, path, body, headers or {})
        response = connection.getresponse()
        result = response.status, dict(response.getheaders()), response.read()
        connection.close()
        return result

    def login(self):
        status, headers, _ = self.request("POST", "/login", "username=admin&password=password", {"Content-Type": "application/x-www-form-urlencoded"})
        self.assertEqual(303, status)
        self.assertIn("HttpOnly", headers["Set-Cookie"])
        self.assertIn("SameSite=Lax", headers["Set-Cookie"])
        return headers["Set-Cookie"].split(";", 1)[0]

    def test_authentication_head_range_and_logout(self):
        self.assertEqual(401, self.request("HEAD", "/protected/test.zip")[0])
        cookie = self.login()
        status, headers, body = self.request("HEAD", "/protected/test.zip", headers={"Cookie": cookie})
        self.assertEqual(200, status)
        self.assertEqual(str(fixture.ZIP_SIZE), headers["Content-Length"])
        self.assertEqual(b"", body)
        status, headers, body = self.request("GET", "/protected/test.zip", headers={"Cookie": cookie, "Range": "bytes=0-9"})
        self.assertEqual(206, status)
        self.assertEqual(self.server.payload[:10], body)
        self.assertEqual(f"bytes 0-9/{fixture.ZIP_SIZE}", headers["Content-Range"])
        self.assertEqual(401, self.request("GET", "/protected/test.zip", headers={"Range": "bytes=0-9"})[0])
        self.assertEqual(303, self.request("GET", "/logout", headers={"Cookie": cookie})[0])
        self.assertEqual(401, self.request("HEAD", "/protected/test.zip", headers={"Cookie": cookie})[0])

    def test_public_file_ranges_invalid_login_and_valid_zip(self):
        self.assertEqual(401, self.request("POST", "/login", "username=admin&password=wrong")[0])
        self.assertEqual(200, self.request("HEAD", "/public/test.zip")[0])
        self.assertEqual(416, self.request("GET", "/public/test.zip", headers={"Range": "bytes=999999999-"})[0])
        with zipfile.ZipFile(io.BytesIO(self.server.payload)) as archive:
            self.assertIsNone(archive.testzip())
            self.assertEqual(["fixture.bin"], archive.namelist())
        self.assertEqual(fixture.ZIP_SIZE, len(self.server.payload))

    def test_nonloopback_binding_is_rejected(self):
        with self.assertRaises(ValueError):
            fixture.FixtureServer(("0.0.0.0", 0))


if __name__ == "__main__":
    unittest.main()
