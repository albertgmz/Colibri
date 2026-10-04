# Local browser cookie fixture

Run from the repository (Python standard library only):

```powershell
python scripts/browser-cookie-fixture.py
```

The server binds only 127.0.0.1 on an ephemeral port and prints its local URL. Synthetic username **admin**, password **password**. Use a temporary browser profile with sync disabled; open that URL and sign in. The server sets a random HttpOnly, SameSite=Lax session cookie in memory. It does not persist account data or log passwords, cookies, URLs or request headers. Console lines show only method, case, authenticated boolean, status and count. Stop with Ctrl+C; sessions expire when the server stops.

Click **Authenticated test ZIP** with the Colibri extension enabled, confirm in Colibri, and verify its 32 MiB ZIP finishes and opens/tests as valid ZIP containing fixture.bin. Every GET, HEAD and byte-range request to `/protected/test.zip` requires that same session cookie. Missing/expired cookies return 401, correct cookies 200 or 206 for a valid range. This tests HttpOnly-cookie capture/replay across the handoff without a real website/account. Click **Public comparison ZIP** at `/public/test.zip` to separate transfer issues from cookie issues. Sign out invalidates the session and clears the cookie; a subsequent protected request must fail 401.

Only one byte range is supported (`bytes=start-end`, `bytes=start-`, or suffix `bytes=-length`); invalid/unsatisfiable ranges return 416 with Content-Range. Download responses include Content-Disposition, Content-Length and Accept-Ranges. Default 64 KiB chunks pause 0.01 s so a browser capture has time to hand off; use `--chunk-delay 0` for an unthrottled comparison or `--port 8765` for an explicit loopback port. Do not interpret this fixture as WAN/authentication-production performance evidence.

Root-owned no-UI checks:

```powershell
python -m unittest discover -s scripts -p test_browser_cookie_fixture.py
```

These tests start only a temporary loopback server, cover login/401/HEAD/authenticated ranges/logout/public comparison and validate the deterministic ZIP CRC. No browser is launched.
