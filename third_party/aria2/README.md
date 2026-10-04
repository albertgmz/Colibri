# aria2 (third-party)

Colibri drives [aria2](https://github.com/aria2/aria2) as a separate executable,
talking to it only through its JSON-RPC interface. aria2 is licensed under
GPLv2 (with an OpenSSL exception); see `COPYING` and `LICENSE.OpenSSL`.

| File | Source |
|---|---|
| `win-x64/aria2c.exe` | `aria2-1.37.0-win-64bit-build1.zip` from the official release `release-1.37.0` |

SHA-256 of `win-x64/aria2c.exe`:
`BE2099C214F63A3CB4954B09A0BECD6E2E34660B886D4C898D260FEBFE9D70C2`

The corresponding source code is included as `source/aria2-1.37.0.tar.xz`
(the official `release-1.37.0` tarball, SHA-256
`60a420ad7085eb616cb6e2bdf0a7206d68ff3d37fb5a956dc44242eb2f79b66b`), as GPLv2
section 3 requires when the binary is redistributed.

On Linux and macOS aria2 is not bundled; Colibri looks for `aria2c` on `PATH`
(install it with your package manager or Homebrew).
