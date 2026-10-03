# Colibri

Colibri is a download manager for Windows, Linux and macOS. The workflow follows Internet Download
Manager (a table of downloads sorted into categories, an Add URL window, the browser handing downloads
over), with a modern Fluent look. The transfers themselves are done by [aria2](https://aria2.github.io/),
which Colibri runs in the background and controls over JSON-RPC. It is written in C# on .NET 10 with
Avalonia 12.

**Status: v1.** Windows has been verified by running the app. Linux and macOS build and pass the unit
tests in CI, but the app has not been run on them yet; expect rough edges there.

## Features

- Downloads table with live progress, speed and time left, search, and navigation by state (All, Active,
  Completed, Failed) and category (Compressed, Documents, Music, Programs, Video, Other). When there are
  no downloads yet, the list shows a hint on how to add the first one.
- Each category can have its own folder; a download's category comes from its file extension.
- Details pane under the table, with a segment bar showing which parts of the file are done.
- Pause, resume, retry, and delete (optionally with the file); downloads survive restarts of the app and
  of aria2.
- Tray icon: close or minimize to the tray, overall progress in the tooltip (and on the taskbar button
  on Windows).
- Single instance: starting Colibri again brings the running window to the front.
- Notifications when a download completes or fails (with Open / Show in folder / Retry buttons on
  Windows and Linux).
- Browser capture for Chrome and Edge (Chromium on Linux and macOS too): downloads matching your rules
  open in Colibri, and links have a "Download with Colibri" menu entry.
- Settings page: start with the system, tray behaviour, theme (system, light, dark), download folders,
  concurrent downloads, connections per server, speed limit, aria2 path, capture rules. Changes save
  immediately.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the version is pinned in `global.json`, with
  roll-forward to newer feature bands).
- aria2 1.x:
  - **Windows:** nothing to install. `aria2c.exe` is in `third_party/aria2/win-x64` and is copied next to
    Colibri by the build.
  - **Linux:** install it with your package manager:
    - Debian, Ubuntu: `sudo apt install aria2`
    - Fedora: `sudo dnf install aria2`
    - Arch Linux: `sudo pacman -S aria2`
    - openSUSE: `sudo zypper install aria2`
  - **macOS:** `brew install aria2` (Colibri also looks in `/opt/homebrew/bin` and `/usr/local/bin`,
    because apps started from Finder do not get your shell's `PATH`).

  You can also point Colibri at any `aria2c` in Settings → Advanced.
- Node.js 22.7 or later, only to run the extension's tests (CI uses Node 24).

## Build, test, run

All commands are run from the repository root.

```sh
dotnet build Colibri.slnx                     # build everything
dotnet test Colibri.slnx                      # all .NET tests (xunit v3 on Microsoft Testing Platform)
dotnet test --project tests/Colibri.Core.Tests # one test project
node --test "extension/tests/*.test.js"      # extension tests (keep the quotes)

dotnet run --project src/Colibri.App                  # start the app
dotnet run --project src/Colibri.App -- --minimized   # start hidden in the tray
```

The UI tests use Avalonia's headless platform, so `dotnet test` needs no display (also on Linux CI).

There is also an end-to-end check of the aria2 engine against a real download (100 MB by default, or
pass your own URL). It is not run by `dotnet test`:

```sh
dotnet run --project tests/Colibri.Engine.Aria2.Smoke [url]
```

**Target frameworks.** `Colibri.App` targets `net10.0-windows10.0.19041.0` when it is built for Windows
(a `win-*` runtime, or no runtime on a Windows machine) and plain `net10.0` everywhere else. The Windows
target is needed for toast notifications. You don't choose this yourself: it follows from the OS and the
`-r` option (see `Directory.Build.props` and decision 2 in `docs/DECISIONS.md`). It does affect the
output folders, for example `src/Colibri.App/bin/Debug/net10.0-windows10.0.19041.0/` on Windows and
`src/Colibri.App/bin/Debug/net10.0/` on Linux and macOS.

## Publishing

There are no installers yet. `dotnet publish` produces a folder you can copy and run.

```sh
# Windows: self-contained (no .NET runtime needed on the target machine)
dotnet publish src/Colibri.App -c Release -r win-x64 --self-contained

# Windows: framework-dependent (smaller; needs the .NET 10 Runtime installed)
dotnet publish src/Colibri.App -c Release -r win-x64 --self-contained false

# Linux
dotnet publish src/Colibri.App -c Release -r linux-x64 --self-contained

# macOS (Apple silicon / Intel)
dotnet publish src/Colibri.App -c Release -r osx-arm64 --self-contained
dotnet publish src/Colibri.App -c Release -r osx-x64 --self-contained
```

Use `--self-contained false` on any of them for a framework-dependent build.

The output lands in `src/Colibri.App/bin/Release/<tfm>/<rid>/publish/`, for example
`bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/` or `bin/Release/net10.0/linux-x64/publish/`.
It contains:

- `Colibri` (`Colibri.exe` on Windows): the app.
- `Colibri.NativeHost` (`Colibri.NativeHost.exe`): the native-messaging host the browser extension talks
  to. It must stay in the same folder as the app.
- Windows only: `aria2/aria2c.exe` with its licence files `aria2/COPYING` and `aria2/LICENSE.OpenSSL`.

Notes:

- Trimming and Native AOT are deliberately off. Colibri is meant to load plugin assemblies at runtime
  later (see [docs/plugins.md](docs/plugins.md)), which trimming and AOT would break.
- macOS: there is no `.app` bundle yet. Run the `Colibri` binary from the publish folder (for example
  from Terminal). It is not signed, so Gatekeeper may block a copy that was downloaded rather than built
  locally.
- Linux: no AppImage, Flatpak or distribution package yet. Run `Colibri` from the publish folder.
- If you move the folder after setting up browser capture, run Settings → Browser → Install / repair
  again so the browsers find the host at its new location.

## Browser extension (Chrome and Edge)

The extension lives in `extension/` (Manifest V3). It is not in a browser store; load it unpacked.

1. **Chrome:** open `chrome://extensions`, turn on **Developer mode** (top right), click **Load
   unpacked** and select the `extension/` folder.
   **Edge:** open `edge://extensions`, turn on **Developer mode**, click **Load unpacked** and select the
   `extension/` folder.
2. The manifest contains a fixed public key, so the extension always gets the same ID,
   `lelenggmjjaffoebgecdpemmjakhofni`, in every browser and from any folder.
3. In Colibri, open **Settings → Browser** and click **Install / repair**. This registers the native host
   (`com.colibri.host`) for the current user only, pointing at the `Colibri.NativeHost` next to the
   running Colibri:
   - **Windows:** writes `%LOCALAPPDATA%\Colibri\NativeMessagingHosts\com.colibri.host.json` and points
     `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.colibri.host` and
     `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.colibri.host` at it (both keys are always
     written).
   - **Linux:** writes `com.colibri.host.json` into `NativeMessagingHosts` under
     `~/.config/google-chrome`, `~/.config/chromium` and `~/.config/microsoft-edge` (or
     `$XDG_CONFIG_HOME` instead of `~/.config`).
   - **macOS:** the same, under `~/Library/Application Support/Google/Chrome`,
     `~/Library/Application Support/Chromium` and `~/Library/Application Support/Microsoft Edge`.

   On Linux and macOS only browsers you have already run (their profile folder exists) are set up. The
   page shows each browser as Installed, Not installed or Needs repair. There is no "remove" button yet.
   Snap and Flatpak browsers cannot start native hosts outside their sandbox.

How it behaves:

- The toolbar popup has a **Capture downloads** switch (on by default) and shows whether Colibri can be
  reached.
- Which downloads are captured is decided by Colibri's settings (Settings → Browser: file extensions and
  minimum size), not by the extension. A download of unknown size passes the size check.
- A captured download opens Colibri's Add URL window and is cancelled in the browser once you see that
  window. If Colibri is closed, the host starts it (hidden in the tray) first. If Colibri can't be
  reached within 20 seconds, the browser just downloads the file itself.
- Downloads from private (incognito / InPrivate) windows are never captured.
- Right-clicking a link offers **Download with Colibri**.
- A build published in the Chrome Web Store or Edge Add-ons would get a different ID. That ID would have
  to be added to the host's allowed origins (`BrowserExtension` in `src/Colibri.Core/Platform`).

Details and the reasoning behind them: decisions 43 to 48 in [docs/DECISIONS.md](docs/DECISIONS.md).

## Where your data lives

Everything is per user, in a `Colibri` folder under .NET's local application data folder:

| OS | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\Colibri` |
| Linux | `$XDG_DATA_HOME/Colibri`, by default `~/.local/share/Colibri` |
| macOS | `~/Library/Application Support/Colibri` |

Inside it:

| File | What it is |
|---|---|
| `colibri.db` | SQLite database with the download list. Includes cookies and headers of downloads, in plain text. |
| `settings.json` | Your settings. |
| `aria2.session` | aria2's session file, so unfinished downloads continue after a restart. |
| `logs/colibri-<date>.log` | The app's log, one file per day, kept 14 days. |
| `logs/native-host-<date>.log` | The browser host's log, kept 14 days. |
| `logs/aria2.log` | aria2's own warnings (may contain full URLs). |
| `NativeMessagingHosts/com.colibri.host.json` | Windows only: the host manifest the browsers are pointed at. |

On Linux and macOS the folder is owner-only (`0700`). Settings → Advanced has a button that opens the
logs folder.

Outside this folder Colibri may also write: the autostart entry when "Start with the system" is on
(Windows `HKCU\...\CurrentVersion\Run`, Linux `~/.config/autostart/colibri.desktop`, macOS
`~/Library/LaunchAgents/com.colibri.app.plist`), the browser registrations above, and on Windows the
per-user registration Windows needs for toast notifications.

The app, the browser host and a second Colibri talk to the running Colibri over a local pipe, and a
per-user mutex (`colibri-instance-<hash>`) keeps it to one instance. Nothing of this is stored in the data
folder:

| OS | Pipe |
|---|---|
| Windows | named pipe `colibri-<hash of your user SID>` |
| Linux | socket `$XDG_RUNTIME_DIR/colibri.sock` (usually `/run/user/<uid>`); without a usable `$XDG_RUNTIME_DIR`, `CoreFxPipe_colibri-<hash of your user name>` in the temp folder (`$TMPDIR`, usually the shared `/tmp`) |
| macOS | socket `$TMPDIR/CoreFxPipe_colibri-<hash of your user name>` (`$TMPDIR` is a per-user folder) |

Details in decision 49 in [docs/DECISIONS.md](docs/DECISIONS.md).

## Project layout

```
Colibri.slnx                   solution
Directory.Build.props          shared build settings, Windows/other target framework switch
Directory.Packages.props       all NuGet versions in one place
src/
  Colibri.Core/                models, contracts and download logic; no UI, no aria2, no OS code
  Colibri.Engine.Aria2/        runs aria2c and talks to it over JSON-RPC (WebSocket)
  Colibri.Platform/            per-OS code: paths, tray check, notifications, autostart, shell, browser registration
  Colibri.Platform.Ipc/        per-OS pipe and mutex names and the Windows foreground handoff, shared by app and host
  Colibri.App/                 the Avalonia app (views, view models, SQLite storage, settings, startup); builds Colibri(.exe)
  Colibri.NativeHost/          native-messaging host between the browser extension and the running app
extension/                     Manifest V3 extension for Chrome and Edge, with Node tests in extension/tests
tests/                         xunit tests per project, plus Colibri.Engine.Aria2.Smoke (real-download check)
third_party/aria2/             aria2c.exe for Windows, its licence files and source code
docs/                          plan, design decisions, plugin guide
```

## Manual test checklist

Run through this before a release, on each OS you ship.

- [ ] Add a URL (Add URL window) and let the download complete; the file is in the category folder.
- [ ] Pause a running download and resume it; it continues where it stopped.
- [ ] Quit Colibri (tray → Exit) in the middle of a download, start it again; the download continues.
- [ ] Close the window: Colibri stays in the tray. Restore it from the tray icon.
- [ ] Capture a download from Chrome: the Add URL window opens with the file name filled in, and the
      browser's own download disappears.
- [ ] The same from Edge.
- [ ] Quit Colibri, then start a matching download in the browser: Colibri starts and shows the Add URL
      window.
- [ ] "Download with Colibri" on a link.
- [ ] A download smaller than the minimum size, or with an extension not in the list, stays in the
      browser.
- [ ] A download from a private window stays in the browser.
- [ ] With capture switched off in the popup, downloads stay in the browser.
- [ ] Start Colibri a second time: no second window, the running one comes to the front.
- [ ] Start with `--minimized`: only the tray icon appears.
- [ ] Delete a download with "Also delete the file" checked: the file is gone from disk. Without it, the
      file stays.
- [ ] Change some settings, restart Colibri: they are kept.
- [ ] Completed and failed notifications appear; their buttons work (Windows, Linux).
- [ ] Turn on "Start with the system", log out and in: Colibri starts in the tray.

## Licence

Colibri is released under the [MIT License](LICENSE).

aria2 is licensed under GPLv2 (with an OpenSSL exception). Colibri does not link to it: it ships
`aria2c.exe` as a separate program on Windows, with `COPYING` and `LICENSE.OpenSSL` next to it, and only
talks to it over JSON-RPC. The matching source code
is in `third_party/aria2/source/`, see [third_party/aria2/README.md](third_party/aria2/README.md). All
third-party components and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## More documentation

- [docs/PLAN.md](docs/PLAN.md): the v1 plan and milestones.
- [docs/DECISIONS.md](docs/DECISIONS.md): design decisions and the reasons for them.
- [docs/plugins.md](docs/plugins.md): writing link resolvers (and, later, engines).
