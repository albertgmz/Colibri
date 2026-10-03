# Decisions

Short records of choices that are not obvious from the code. Newest at the bottom.

## 1. `.slnx` solution and central package management

The solution uses the XML `.slnx` format (the .NET 10 default for `dotnet new sln`); it is
readable and merges cleanly. All NuGet versions live in `Directory.Packages.props`, so project
files only name packages and every project gets the same version. Shared build settings
(nullable, implicit usings, language version) live in `Directory.Build.props`.

## 2. App target framework depends on the build OS

`Colibri.App` and `Colibri.App.Tests` target `net10.0-windows10.0.19041.0` when building for
Windows (a `win-*` runtime, or no runtime on a Windows machine), and plain `net10.0` otherwise.
The Windows TFM is needed for WinRT toast notifications in `Colibri.Platform`, but Linux and
macOS builds cannot use Windows-only assets. The switch is the `ColibriAppTfm` property in
`Directory.Build.props`. `Colibri.Platform` builds both TFMs everywhere; `EnableWindowsTargeting`
is set for all projects so Linux and macOS can also build or publish for `win-x64`.

## 3. xunit.v3 3.2.2 on Microsoft Testing Platform

`Avalonia.Headless.XUnit` 12.1.3 depends on `xunit.v3.extensibility.core` 3.2.2, so all test
projects use `xunit.v3` 3.2.2 (the latest 3.2.x; 4.x would not match). `dotnet test` on the
.NET 10 SDK runs through Microsoft Testing Platform, enabled in `global.json`; xunit.v3 3.x
supports it natively, so `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not needed.

## 4. Avalonia FluentTheme with a ListBox navigation pane

The app uses Avalonia's built-in `FluentTheme` and a standard `ListBox` for the navigation
pane, not FluentAvaloniaUI's `NavigationView`. FluentAvaloniaUI 3.1.0 supports Avalonia 12.1,
but it replaces `FluentTheme` with its own `FluentAvaloniaTheme`, which conflicts with the
decision to use Avalonia's Fluent theme. The layout is achievable with standard controls.

## 5. Serilog for the rolling log file

`Microsoft.Extensions.Logging` has no file provider. Serilog (`Serilog.Extensions.Hosting` +
`Serilog.Sinks.File`) plugs into it and writes a rolling file, so app code still logs through
`ILogger<T>` and only startup knows about Serilog.

## 6. LinkResolverPipeline skips resolvers that throw

A resolver that throws is logged as a warning and skipped; the next resolver is asked. One
broken resolver (for example a site-specific one hitting a changed page) must not stop the
direct-link fallback from working. Cancellation of the caller's token is not swallowed.

## 7. Cookies and headers are stored in plain text, per user

A download's cookies and extra headers are stored in the local SQLite database and in aria2's
session file, in plain text, inside the per-user data folder. Retries and aria2 session recovery
then keep the authentication the original request had. The folder is per-user but not
encrypted. URLs written to logs are redacted (`UrlPolicy.Redact`).

## 8. Colibri always chooses the output file name

aria2's `out` option is always set from our sanitized file name, which overrides the name the
server sends in `Content-Disposition`. This keeps sanitizing, categories and unique names under
our control. For browser captures the browser supplies its own resolved file name, so the
server's name is still respected there.

## 9. Hand-written JSON-RPC client over WebSocket

The aria2 client is about 250 lines on `ClientWebSocket` and `System.Text.Json` (`JsonNode`), with
no extra package. StreamJsonRpc expects its own message framing and conventions, and does not handle
aria2's details (secret token as the first parameter, numbers sent as strings, notifications without
an id). A small client is easy to read and to test against a fake transport.

## 10. Colibri chooses aria2 GIDs

`AddAsync` generates a random 16-hex-character GID and passes it in aria2's `gid` option. The handle
is known before aria2 answers, so it can be stored with the download right away, and aria2 keeps the
same GID across restarts through its session file.

## 11. `--file-allocation=none`

aria2 does not reserve disk space up front. `prealloc` writes the whole file before downloading
(slow for large files) and `falloc` is not supported on every file system; `none` works everywhere.

## 12. forcePause and forceRemove

Pause and remove use `aria2.forcePause` and `aria2.forceRemove`. The plain versions only add
BitTorrent tracker announcements, which can take seconds; Colibri does not use BitTorrent, and the
forced versions react at once. The download can still be resumed from its `.aria2` control file.

## 13. aria2 restart policy

When aria2 exits without being asked to, it is restarted after 1 s, 2 s, then 5 s. After three quick
failures in a row the engine state becomes `Failed`; aria2 staying up for 60 s resets the count. A
restart reloads the session file, so listeners re-read all downloads when the state returns to `Running`.

## 14. aria2's RPC secret is passed in a config file, not on the command line

On Linux and macOS any local user can read other users' command lines (`ps`, `/proc/<pid>/cmdline`),
so `--rpc-secret=...` would let them control aria2. Each launch writes a fresh random secret to
`aria2-rpc.conf` in the data folder (owner-only `0600` on Unix, created before the secret is written;
on Windows the per-user LocalAppData ACL covers it) and passes `--conf-path`. aria2 reads the file only
at startup, so it is deleted as soon as the RPC connection works, and again on stop. Naming our own
config file also keeps aria2 from loading a user's personal `~/.aria2/aria2.conf`.

## 15. aria2.log is not redacted

aria2 writes its own `aria2.log` (warnings only) to the per-user logs folder; it can contain full URLs,
query strings included, which Colibri cannot redact.

## 16. One row list behind a DataGridCollectionView, re-filtered only when needed

Every download has one `DownloadItemViewModel`, created once and updated in place; all of them live in
one `ObservableCollection` shown through a `DataGridCollectionView` that filters (navigation entry +
search text) and sorts. The filter is re-applied only when the navigation entry or the search text
changes, or when a row moves in or out of the current filter; a plain progress tick never refreshes
the view. Manager events are posted to the UI thread once per batch.

## 17. Progress is written to the database at most every 5 s per download

The poll tick updates downloads in memory every second (every 5 s while the window is hidden). State
changes are saved at once; progress-only changes are saved when the last save of that download is at
least 5 s old, and whatever is still unsaved is written on exit. After a crash the database is at most
a few seconds behind; aria2's own `.aria2` control file holds the exact progress.

## 18. Reconcile rules (startup and aria2 restart)

For each unfinished download: if aria2 knows the GID, its state is adopted, except that the user's
choice wins (paused in Colibri but running in aria2 is paused again, and the other way round). If
aria2 does not know it, it is added again with the same GID, folder and name, paused if it was paused,
and aria2 continues the partial file from its control file. Completed and failed downloads are left
alone (failed ones are retried only on request), and aria2 downloads that are not in the database are
removed from aria2 (see 32). While Colibri shows a download as paused, an "active" report from aria2 is
ignored, because `forcePause` takes a moment to apply.

## 19. Window chrome behind `IWindowChrome`

On Windows the main window extends its content into the title bar and asks for Mica, falling back to
an opaque window. `IWindowChrome` only sets the window hints and two style classes ("extended", "mica");
the XAML styles react to them, so views never check the OS. The implementation is chosen in the app's
DI registration: it uses Avalonia types, so it cannot live in `Colibri.Platform`, and this is the only
OS check in `Colibri.App`. With an extended client area Avalonia 12 draws the title and the caption
buttons itself; its full-screen button is hidden with a style.

## 20. Strings.resx generated by MSBuild

The strongly typed `Strings` class is produced by the SDK's `GenerateResource` task during
`dotnet build` (`StronglyTypedLanguage` and related metadata on the `EmbeddedResource`), so no Visual
Studio designer file is checked in. XAML uses it through `{x:Static res:Strings.Key}`. A Spanish
`Strings.es.resx` can be added next to it later without code changes.

## 21. Temporary `IShellService` (superseded)

Milestone 3 used a temporary `ProcessShellService`. It has been replaced by the per-OS
implementations described in entry 36.

## 22. Engine add takes an optional handle and a paused flag

`IDownloadEngine.AddAsync` accepts an existing handle and a "start paused" flag. Reconcile and retry
add a download again under its old GID (so the handle in the database stays valid and aria2 finds its
control file) and can restore a paused download without it starting first.

## 23. App: single instance and the local pipe protocol

The first Colibri process of a user owns a named mutex (`colibri-instance-<hash>`, current user only,
not per session: on Unix each terminal is its own session, and the pipe name is per user); later processes send `activate` with their arguments over the local pipe and exit with
code 0 before starting anything, aria2 included. A mutex was chosen over "whoever creates the pipe
first": on Linux and macOS .NET replaces a leftover socket file when a pipe server starts, so a second
process could take the pipe over silently, while named mutexes work across processes on all three
systems and are released by the OS when the owner dies.

The pipe is `colibri-<first 16 hex of SHA-256(user name)>`, created with `PipeOptions.CurrentUserOnly`
(on Unix it is a socket in the temp folder; the short name keeps macOS under its ~104-character socket
path limit). One request and one response per connection, each a UTF-8 JSON object on one line, at most
1 MiB; 10 s per connection. Requests: `activate {args}` and `add {url, finalUrl?, fileName?, referrer?,
cookies?, userAgent?, size?, mimeType?, headers?}`; response `{ok, error?}`. Unknown types, wrong field
types, invalid URLs (`UrlPolicy`), header values with control characters, negative or fractional sizes
and over-long strings are rejected with an error response; unknown fields are ignored. The same pipe
will carry the native-messaging host's requests (browser capture). `finalUrl` is validated but not used
yet; Colibri downloads `url` and lets aria2 follow the redirects.

## 24. App: headers from the browser that are not forwarded

`HttpHeaders.IsForwardable` drops Range, Accept-Encoding, Content-Length, Host, Connection,
Transfer-Encoding, Upgrade, TE, Keep-Alive and every `Proxy-*` header (aria2 sets them itself or they
describe the browser's own connection; Accept-Encoding would make servers send a compressed body that
aria2 saves as is), and Cookie, Referer and User-Agent, which come in their own fields. These are dropped
quietly because browsers report them routinely; a header with an invalid name or value rejects the
whole request.

## 25. App: a browser capture is acknowledged when the Add URL window is shown

The `add` request is answered `ok` as soon as it is valid and the prefilled Add URL window is on screen
(topmost until it opens, so it appears over the browser even with the main window hidden). The browser
then cancels its own download. If the user cancels Colibri's window, the download is dropped; that is
intended, as in other download managers. An invalid request is answered `ok: false` and the browser
keeps its download.

## 26. App: settings are saved as they change

The settings page has no Save button: toggles, the theme and the numbers apply and save at once; text
boxes (folders, aria2 path, extensions) when they lose focus. Engine options are sent to aria2 right
away; a changed aria2 path restarts aria2. A relative folder is refused with a message and not saved,
numbers are clamped to their range and an emptied number box keeps the old value. The shared
`AppSettings` object is read by the download manager on other threads, so collections in it are
replaced rather than changed in place.

## 27. App: segment bar drawn from aria2's bitfield

`DownloadItem` carries aria2's `bitfield` and `numPieces`, copied from each status snapshot. They are not
stored in the database: they are only meaningful while aria2 knows the download, and aria2 reports them
again on the first poll. The details pane's segment bar turns the bitfield (highest bit of the first byte
is piece 0) into runs of consecutive finished pieces and draws one rectangle per run, so a file with
thousands of pieces is a handful of rectangles; the runs are computed when the bitfield changes, not on
every render. Without a bitfield (finished, or never started this session) it draws a plain bar from the
progress.

## 28. App: polling every second while visible, every 5 s while hidden

The window reports its visibility to the download manager: 1 s while it is shown, 5 s while it is
minimized, hidden in the tray or never shown (`--minimized`). The tray tooltip and taskbar progress are
updated from the same tick and only when their value changes, which is the throttling they need.

## 29. App: without a tray icon the window is never hidden

`ITrayAvailability` is asked once at startup. Without a tray (some Linux desktops) closing the window
exits, minimizing only minimizes, `--minimized` starts minimized to the taskbar instead of hidden, and the
status bar and settings page say that the tray is not available. The app always runs with
`ShutdownMode.OnExplicitShutdown`; the tray menu's Exit and a real close run the same exit: hide the
window, stop the pipe server, stop the download manager (aria2 saves its session), then shut down.

## 30. App: delete asks inside the window

Delete (command bar or context menu) opens a confirmation over the window with the download's name or
the number of downloads and an "Also delete the file(s)" box, unchecked each time. The separate "Delete
with file" menu entry is gone.

## 31. Engine handles are reserved before the add

`IDownloadEngine.CreateHandle` returns a new handle (for aria2 a random GID). The download manager stores
it with the new download before calling `AddAsync` with it. If the add times out but reached aria2 anyway,
the next poll or reconcile finds the download under the stored handle instead of treating it as foreign.

## 32. Reconcile removes aria2 downloads that are not in the database

Colibri's aria2 session is private, so a download in it that the database does not know is left over (for
example deleted while aria2 was not running). Reconcile removes it from aria2 (`forceRemove` and
`removeDownloadResult`); its files are never deleted.
||||||| 2e6b6d4

## 33. Platform: Windows toasts through Microsoft.Toolkit.Uwp.Notifications 7.1.3

Toasts use `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (MIT). `CommunityToolkit.WinUI.Notifications`
7.1.2 was a short-lived rename of the same code; 7.1.3 of the original package came out later, is what
Microsoft's toast documentation uses, and both are no longer developed. Its `ToastNotificationManagerCompat`
makes toasts and their buttons work for an unpackaged exe: on first use it registers, per user and
permanently, an app id derived from the exe path (`HKCU\Software\Classes\AppUserModelId\<id>`), a COM
activator (`HKCU\Software\Classes\CLSID\{guid}\LocalServer32` = `"<exe>" -ToastActivated`) and an icon
under `%LOCALAPPDATA%\ToastNotificationManagerCompat`. `Uninstall()` is not called on exit, because it
also clears the toasts in Action Center and their buttons would stop working; removing the registration
is an uninstaller's job. Clicking a toast after Colibri exited starts the exe with
`-ToastActivated -Embedding` and delivers the click by COM once the notification service is created, so
the app must create that service at startup and ignore those arguments. Toast and button arguments are
`<action>;<download id>`. Clicking a "completed" toast opens the file; clicking a "failed" toast only
dismisses it (retrying needs the button). The package depends on System.Drawing.Common 4.7.0, which has a
critical advisory (GHSA-rxg9-xrhp-64gj), so `Colibri.Platform` references System.Drawing.Common 10.0.12
directly. The toast code is compiled only for the Windows target framework (`#if WINDOWS`); the plain
net10.0 build running on Windows (only tests do that) shows no notifications.

## 34. Platform: Linux desktop integration over D-Bus with Tmds.DBus.Protocol 0.95.1

Notifications (`org.freedesktop.Notifications`), "show in folder" (`org.freedesktop.FileManager1`) and the
tray check talk to the session bus through `Tmds.DBus.Protocol` (MIT), the library Avalonia itself uses
for D-Bus. Avalonia 12.1.3 asks for 0.94.1; 0.95.1 only fixes match-rule keys and has the same public API,
so the app ends up with one copy at 0.95.1. One shared connection is opened on first use. Without a session
bus or a notification server, notifications do nothing (logged, never thrown); this is checked once per
run, at the first notification. `ShowItems` is given up on after 5 s (D-Bus calls have no timeout of
their own) and the folder is opened instead. Notification buttons are
D-Bus actions; the "default" action (clicking the notification) opens a completed download. Servers that
announce `body-markup` get the body with `&`, `<` and `>` escaped, so file names show as written.

## 35. Platform: macOS notifications have no buttons

A plain .NET process is not a signed app bundle, and macOS only lets bundles use the notification API
(UNUserNotificationCenter). Colibri runs `osascript -e 'display notification ...'` instead (the script is
one argument, with AppleScript string escaping; no shell). These notifications have no buttons, report no
clicks (`ActionInvoked` never fires on macOS), and appear under "Script Editor" in the notification
settings.

## 36. Platform: per-OS "open" and "show in folder"

Replaces the temporary service of decision 21. Windows opens files through ShellExecute and selects the
file with `SHOpenFolderAndSelectItems`, avoiding `explorer.exe /select,` whose parsing breaks on commas
in paths. Linux uses `xdg-open`, and `FileManager1.ShowItems` to select the file, falling back to opening
the folder. macOS uses `open` and `open -R`. Helper programs get each path as a separate argument, never
through a shell. A missing file is logged; "show in folder" then opens its folder if that still exists.

## 37. Platform: autostart entries

Windows: value `Colibri` = `"<exe>" --minimized` under `HKCU\...\CurrentVersion\Run`. Linux:
`$XDG_CONFIG_HOME/autostart/colibri.desktop` (default `~/.config/autostart`), launching `$APPIMAGE` when
Colibri runs from an AppImage. macOS: `~/Library/LaunchAgents/com.colibri.app.plist` with RunAtLoad;
launchctl is not called, so it takes effect at the next login. "Enabled" means the entry starts this
copy of Colibri: on Linux and macOS the file must match exactly what Colibri writes, on Windows the Run
value must point at this exe. Turning Colibri off in Windows Task Manager's Startup tab is stored by
Windows separately and is not detected.

## 38. Platform: taskbar progress on Windows only

Windows shows progress on the taskbar button through `ITaskbarList3` (called on the UI thread). Linux has
no API every desktop supports (the Unity launcher API works only on some docks), and a Dock badge on
macOS is out of scope; both get a no-op.

## 39. Platform: Linux tray detection

Avalonia's Linux tray icon uses the StatusNotifierItem D-Bus protocol, which needs a
`org.kde.StatusNotifierWatcher` on the session bus (KDE, XFCE, Cinnamon and others; GNOME only with the
AppIndicator extension). The tray counts as available only when that name has an owner; any error
counts as unavailable. Windows and macOS always have one.

## 40. Platform: notification texts come from the app

`Colibri.Platform` has no resources. The app passes a `NotificationTexts` record built from its
`Strings.resx` to `AddColibriPlatform(notificationTexts)`; without one, English defaults are used.
