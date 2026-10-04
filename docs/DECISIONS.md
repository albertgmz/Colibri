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

## 7. Cookies and headers are stored in plain text, per user (superseded by 57)

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
same GID across restarts through its session file (superseded by 57).

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
restart reloads the session file (superseded by 57), so listeners re-read all downloads when the state returns to `Running`.

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

An early version used a temporary `ProcessShellService`. It has been replaced by the per-OS
implementations described in entry 36.

## 22. Engine add takes an optional handle and a paused flag

`IDownloadEngine.AddAsync` accepts an existing handle and a "start paused" flag. Reconcile and retry
add a download again under its old GID (so the handle in the database stays valid and aria2 finds its
control file) and can restore a paused download without it starting first.

## 23. App: single instance and the local pipe protocol

The first Colibri process of a user owns a named mutex (its name comes from entry 49; current user only,
not per session: on Unix each terminal is its own session, and the pipe name is per user); later processes send `activate` with their arguments over the local pipe and exit with
code 0 before starting anything, aria2 included, once the running Colibri accepts them (see entry 50 otherwise). A mutex was chosen over "whoever creates the pipe
first": on Linux and macOS .NET replaces a leftover socket file when a pipe server starts, so a second
process could take the pipe over silently, while named mutexes work across processes on all three
systems and are released by the OS when the owner dies.

The pipe's name or socket path comes from entry 49; it is created with `PipeOptions.CurrentUserOnly`. A
Colibri that is still exiting is waited for (entry 50). One request and one response per connection, each a UTF-8 JSON object on one line, at most
1 MiB; 10 s per connection. Requests: `activate {args}` and `add {url, finalUrl?, fileName?, referrer?,
cookies?, userAgent?, size?, mimeType?, headers?}`; response `{ok, error?}`. Unknown types, wrong field
types, invalid URLs (`UrlPolicy`), header values with control characters, negative or fractional sizes
and over-long strings are rejected with an error response; unknown fields are ignored. The same pipe
carries the native-messaging host's requests (browser capture), which adds `ping` and `config` (entry 44). `finalUrl` is validated but not used
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
(topmost until it opens, and on Windows allowed to the foreground by the native host, entry 51, so it appears
over the browser and has the keyboard focus even with the main window hidden). The browser
then cancels its own download. If the user cancels Colibri's window, the download is dropped; that is
intended, as in other download managers. An invalid request, or one that arrives while Colibri is exiting,
is answered `ok: false` and the browser keeps its download.

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

Delete (command bar or context menu) opens a confirmation over the window below the command bar, which is
disabled meanwhile (a translucent layer over the search box let other windows show through the Mica window
on Windows), with the download's name or
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
`<action>;<download id>`. Clicking a "completed" toast opens the file; clicking a "failed" toast shows
Colibri's window (retrying needs the button). How a click reaches the app is described in entry 41. The package depends on System.Drawing.Common 4.7.0, which has a
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
D-Bus actions; the "default" action (clicking the notification) opens a completed download and shows
Colibri's window for a failed one. Servers that
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

## 41. App: notification clicks, including a toast clicked after Colibri exited

The notification service is created, and `DownloadNotifier` subscribes to it, in `Program.Main` right after the
host is built, in the primary instance only. On Windows creating the toast service registers the COM activator,
so a click can be delivered from then on. Actions are not carried out until the downloads are loaded and the
window exists (`DownloadNotifier.StartHandlingActions`); until then they wait. The download is looked up in the
download manager when the action runs, because a toast may belong to an earlier session. `Activate` (a click on
a "failed" notification) shows and restores the main window.

The three Windows cases:

- Colibri is running: Windows calls the COM activator in the running process; no new process starts.
- Colibri is not running: Windows starts `"<exe>" -ToastActivated -Embedding`. That process takes the
  single-instance mutex, starts normally (window shown; the two arguments mean nothing to Colibri, like any
  unknown argument) and receives the click once the toast service exists.
- Colibri is running but Windows still starts a new process (only if the click lands in the moment before the
  running Colibri has created its toast service, or while it is exiting): the new process is a secondary
  instance, forwards `activate` with its arguments over the pipe (the running Colibri shows its window) and
  exits with code 0. The click itself is lost in that case; the user can click again.

## 42. The executable is `Colibri`

The app project is still `Colibri.App`, but its assembly is named `Colibri` (`Colibri.exe` on Windows), with
`AssemblyTitle` and `Product` set to `Colibri`. Windows shows the title as the sender of toasts and in Task
Manager. Avalonia resource URIs use the assembly name (`avares://Colibri/...`). Toast registration is keyed by
the exe path (entry 33), so a renamed or moved exe registers again; the old entries stay until removed.

## 43. Browser capture: the download is held in `onDeterminingFilename`

The extension decides in `chrome.downloads.onDeterminingFilename`, not in `onCreated`. The browser does not
name or finish a download until every such listener has called `suggest()`, and by then it knows the server's
file name (Content-Disposition) and size, which the capture rules need; `onCreated` usually has neither, and
pausing from there races with small files that finish first (and with "Ask where to save", whose dialog would
already be open). This is how Colibri pauses a download in the browser: the download is held there while the host is
asked. If Colibri answers `ok`, the browser download is cancelled, `suggest()` releases it, and once it has
stopped it is erased from the download list (Edge writes a cancelled download to its history only after the
release, so an earlier erase let it come back after a restart). In every other case (capture switched off,
rules do not match, private window, a URL that is not http/https/ftp, host not installed, Colibri not
answering, an error, or no answer within 20 s) `suggest()` is called without a name and the browser carries on
as if the extension were not there; nothing has to be resumed. A late `ok` after the 20 s timeout can leave the
file downloading in both places; it cannot lose it. Downloads from private windows are never captured, because
Colibri would keep them in its history. Colibri downloads the original URL and aria2 sends the cookies on every
request, redirects included, so cookies (those of the original URL) go along only when the browser's final URL
is on the same host; after a redirect to another host none are sent, and a download that needs them fails in
Colibri instead of leaking them. "Download with Colibri" on a link sends the link, its cookies and the page as
referrer (only the page's origin for a link to another site); when that fails, the toolbar button shows a red
"!" for 5 s. Only one extension can
decide a file name; another extension with an `onDeterminingFilename` listener (another download manager)
competes with this one.

## 44. Browser capture: rules come from Colibri through a `config` request

The capture rules (extensions and minimum size, from the settings page) are asked for with `{"type":"config"}`
and cached in `chrome.storage.local`; until the first answer the extension uses the defaults of `AppSettings`
(a Node test compares the two lists). A download of unknown size passes the minimum-size check. The extension
refreshes the rules when its service worker starts and when the popup opens, but only after a `ping` answered
`ok`: the host starts Colibri for a `config` request (entry 45), and the service worker starts again whenever an
event wakes it, so asking for the rules unconditionally would start Colibri on every browser start and on
unrelated downloads. The on/off switch lives in the extension (`chrome.storage.local`, on by default).

## 45. Browser capture: the native host starts Colibri when it is needed

For `add` and `config` the host first tries the local pipe for 1 s; if nothing listens it starts `Colibri(.exe)`
from its own folder with `--minimized` and waits up to 12 s for the pipe, then up to 5 s for the answer (Colibri
answers an `add` once the Add URL window is shown, entry 25). The 18 s in total stay within the extension's 20 s. `ping` never starts Colibri; it answers
`{"ok":false,"error":"app-not-running"}`, which the popup shows. Colibri is started with fresh pipes for its
standard streams that the host closes at once, and on Windows the host first makes its own standard handles
non-inheritable: .NET lets a child inherit every inheritable handle, and a Colibri holding the browser's pipes
would keep them open after the host exits (on Unix only the standard streams are inherited, and they are
replaced). On Windows, Colibri started this way keeps running when the browser closes. Referencing the host
project from `Colibri.App` makes the SDK copy the host's apphost, dll, runtimeconfig and deps files next to
`Colibri.exe`, in the build output and in `dotnet publish` (built for the same runtime identifier, also
self-contained); the host references only `Colibri.Core` and `Colibri.Platform.Ipc` (entry 49), never the Windows
build of `Colibri.Platform`.

## 46. Browser capture: one message per host process

The extension uses `runtime.sendNativeMessage`, so the browser starts a host process per message and uses its
first answer. The host still reads messages until stdin closes, so a `connectNative` port also works. Frames are
a 32-bit length in native byte order plus UTF-8 JSON; a frame over 1 MiB is answered with
`message-too-large` and ends the host, a stream ending inside a frame ends it without an answer. Only `ping`,
`config` and `add` (without `headers`, which the extension never sends) are accepted, validated by the same code
as the local pipe (`IpcProtocol`), and only the validated request is passed on. Colibri trims the rules it sends
to what the host accepts (letters and digits, at most 256 extensions). stdout carries nothing but frames: `Console.Out` and `Console.Error` are
replaced with null writers and every failure is caught and logged. The host logs to
`logs/native-host-yyyyMMdd.log` (14 days) in Colibri's data folder: the calling origin, the request type and,
for `add`, the URL redacted by `UrlPolicy.Redact`; never cookies or query strings.

## 47. Browser capture: the extension ID comes from a key in the manifest

`manifest.base.json` in `albertgmz/colibri-browser-integration` carries a public key (`key`), so the unpacked extension has the same ID
(`lelenggmjjaffoebgecdpemmjakhofni`) in every folder and browser; Core's `BrowserExtension.Id` holds it, and a test
recomputes it from a pinned copy of the key. The private key is not in the repository: loading unpacked does not need it. A
build published in a browser store gets the store's own ID, which then has to be added to the host's allowed
origins. The host manifest allows only this origin.

## 48. Browser capture: per-user registration on each OS

"Install / repair" on the settings page registers the host next to the running Colibri, for the current user
only. Windows: the manifest is written to `%LOCALAPPDATA%\Colibri\NativeMessagingHosts\com.colibri.host.json`
and its path is the default value of `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.colibri.host` and of
the same key under `Microsoft\Edge` (both always written). Linux: `com.colibri.host.json` in
`NativeMessagingHosts` under `google-chrome`, `chromium` and `microsoft-edge` in `$XDG_CONFIG_HOME` (default
`~/.config`). macOS: the same under `~/Library/Application Support/Google/Chrome`, `Chromium` and
`Microsoft Edge`. On Linux and macOS only browsers whose profile folder exists are listed and set up, and the
host gets its owner's execute bit. A browser shows "Needs repair" when its entry points to another manifest, the
manifest is unreadable, the host path (case-insensitive on Windows) or the origins differ, for example after
Colibri was moved, or the registered host file no longer exists. Registering fails when the host file is missing. The path registered is wherever Colibri runs
from, so an AppImage (temporary mount) or a translocated macOS app needs "Install / repair" after each start, and
Snap or Flatpak browsers cannot start hosts outside their sandbox. There is no "remove"
action.

## 49. Local pipe and mutex names per OS, in `Colibri.Platform.Ipc`

`IIpcEndpointProvider` gives the pipe name (or socket path) and the single-instance mutex name. The app and the
native host must agree on them, but the host cannot reference `Colibri.Platform` (entry 45), so the providers live
in a small project of their own, `Colibri.Platform.Ipc` (net10.0 only), referenced by both. `IpcPlatform` there picks
the implementation; it is not done in `AddColibriPlatform` because the app needs the names before dependency
injection exists, and the host has none.

- Windows: `colibri-<16 hex>` and `colibri-instance-<16 hex>` from the SHA-256 of the user's SID. Pipe names and the
  machine-wide mutex are visible to every user; two accounts with the same user name (a domain and a local "bob")
  have different SIDs.
- Linux: the socket is `$XDG_RUNTIME_DIR/colibri.sock` (a 0700 folder of the user's own, usually
  `/run/user/<uid>`), so another user cannot create it first in the shared `/tmp` and block Colibri. .NET uses a
  rooted pipe name as the socket path itself (`PipeStream.Unix.cs`, `GetPipePath`). Without a usable
  `$XDG_RUNTIME_DIR` (unset, relative, missing, or a path over the 107 bytes a socket path may have) the names below
  are used.
- macOS, and the Linux fallback: the hash of the user name, as before; .NET puts the socket in `$TMPDIR` as
  `CoreFxPipe_colibri-<16 hex>`. On macOS `$TMPDIR` is a per-user 0700 folder and the path (about 85 characters)
  stays under the 103 a macOS socket path may have.

The mutex uses `CurrentUserOnly`, which on Unix gives each user a namespace of their own for mutex names. The Linux
socket depends on `$XDG_RUNTIME_DIR` but the mutex does not, so the app and the host must see the same value: a
browser in a sandbox that changes it (some Snap packages) or a shell after `su` would look for another socket.

## 50. A new Colibri waits for one that is starting or exiting

Exit refuses `activate` (as it refuses `add`, entry 25), stops the pipe and releases the mutex only when the process
ends, after aria2 has saved its session; a Colibri that is still starting opens its pipe last. A Colibri started in
either moment finds the mutex taken and its arguments not accepted. For up to 15 s (`InstanceElection`) it then waits
half a second at a time for the mutex, becoming the primary instance when it is released, and asks the running
Colibri again in between. Only after that does it log an error and exit with code 1.

## 51. Windows: the native host lets Colibri take the foreground

Windows lets a background process bring its window to the front only in some cases, so the Add URL window for a
browser capture could open without the focus, or behind the browser. The browser starts the host and is the
foreground app when the user starts a download, so the host may pass that right on: for an `add`, once connected,
it asks Windows for the pipe server's process (`GetNamedPipeServerProcessId`) and calls `AllowSetForegroundWindow`
for that process only, rather than `ASFW_ANY` for every process. The right lasts until the user's next input
elsewhere; Colibri's own `Activate` (with a moment of `Topmost`) then works. `IForegroundHandoff` does nothing on
Linux and macOS, which have no such rule; there the window manager decides.

## 52. The Add URL window passes a file name or folder only when it was chosen

The Add URL window shows a file name taken from the last part of the URL and the category folder that goes with
it, but these are only a preview: a link resolver may know the real name (a share page whose URL ends in `download`).
So the window passes a file name to `DownloadManager.AddAsync` only when the user edited it or the browser supplied
it with a captured download, and a folder only when the user typed one or picked one with Browse (even the folder
already shown). Otherwise it passes null, and the name the resolver suggests decides the name and, through its
extension, the category folder. A name or folder box the user clears counts as not chosen again. As before, a file
name override applies only when the link resolves to a single download.

## 53. Layout preferences are additive and distinct from responsive visibility

V2 adds a bounded layout object to settings without changing v1 folders or download
data. Stable column keys store order, width, visibility and sort. Responsive hiding
does not change those preferences. Geometry saves after a 500 ms debounce and is
flushed before application shutdown; disconnected screens cannot strand the title bar.
The default is 960 x 600 with a 640 x 400 minimum, compact 28 px rows, a 160 px
sidebar and a 24 px empty details bar. Splitters and the header grip are draggable.
The Layout menu supplies density, three toolbar modes, sidebar collapse and reset.

Five accent choices use the Fluent palette's Accent property, following the
[Avalonia theme API](https://docs.avaloniaui.net/docs/styling/themes). The existing
system, light and dark choices remain. Free destination space is a Core platform
contract, queried on a worker because DriveInfo has no asynchronous API, at most
once per 30 seconds. Unknown space stays unknown. The network status currently
reports the default route; proxy and adapter policy are described in
[network policy](NETWORK-POLICY.md).

Duplicate URL confirmation precedes adding another item. Resume uses the existing
item; re-download and rename retain the manager's unique-path reservation. Average
speed uses measured active transfer time rather than time since an
item was added, which would count queues and pauses as transfer time.

## 54. Focused Settings pages and a toolbar Layout command

Settings retains its existing controls and persistence model, grouped into General,
Appearance, Downloads, Browser integration and Advanced pages. At 760 px and above,
a compact sidebar chooses the page; below that a keyboard-accessible selector takes
its place. Each page scrolls independently. Layout uses the same Fluent icon/label
and density styles as the other toolbar commands. Narrow label mode moves search
to a separate row without changing stored toolbar preferences.

## 55. Detail views use engine facts and bounded session observations

Core exposes immutable detail snapshots: aria2 piece bitfields, server rows and
per-download option values, plus observed speed and event history. Missing facts
remain explicitly unknown; server rows do not fabricate connection byte ranges.
Speed is sampled at most once per second and bounded to 300 samples/five minutes.
Average speed excludes paused/queued intervals; these measurements restart with
the app session. Paused downloads retain options even when getServers reports no
active download. Other live RPC failures remain errors. Compact tabs and scrollable
content fit small panes; popouts share Core's selection and platform window chrome.

## 56. Browser confirmation owns transfer rollback

Protocol v2 hello discovers a compatible native host without starting the desktop
app. Explicit add, bulk-add and open may start it. A pending capture acknowledges
the dialog, while accepted ownership follows user confirmation and successful
acquisition. Duplicate resume uses the same acceptance boundary.

An acquisition lease serializes confirmation commands and delays terminal rejection
until rollback finishes. Rollback verifies pause, then removes the known handle if
necessary, and finally stops the engine if individual cleanup cannot be established.
This last resort can pause unrelated transfers. Failure to stop or persist rollback
keeps the capture pending and shows manual attention in the extension. Cancellation
returns its current authoritative state promptly rather than waiting past native
response deadlines. A lost port/reply still cannot establish ownership; browser
fallback can duplicate an already accepted app transfer in that unavoidable case.
The protocol does not claim exactly-once delivery.

## 57. Protected request headers replace plaintext aria2 sessions

SQLite schema 3 encrypts the entire request-header dictionary, including cookies
and unknown custom credential headers. Windows uses current-user DPAPI with the
download ID as entropy. Linux uses AES-GCM with a key in the user's Secret Service;
macOS uses AES-GCM with a Keychain key. Unix providers use native OS APIs/existing
D-Bus support, adding no LGPL dependency. Missing/locked stores fail closed;
decryption never creates a replacement key. Provider exceptions expose no payload.

V1/v2 migration is transactional. Completed rows discard credentials; secure_delete,
checkpoint and compaction remove legacy live-page/WAL plaintext. Existing backups,
filesystem snapshots and managed-memory copies cannot be securely erased by this
migration. URL query data remains download metadata. Unix providers compile and
have fake-store crypto coverage; actual OS vault integration must be verified there.

aria2 does not load or save session files because they serialize headers in plaintext.
Only after protected repository initialization succeeds does Core remove the legacy
session and its temporary file. Failure prevents startup. Core reconstructs stable
GIDs and options from its repository, while aria2 control files preserve partial
pieces. This supersedes the session-based recovery described in earlier decisions.

## 58. Fluent palettes are separate from theme and accent

Warm preserves the existing appearance; Graphite, Ocean and Forest select neutral
surfaces independently of light/dark/system mode and accent. Canonical design
tokens generate app resources and extension CSS. An additive config reply carries
the selected palette without changing protocol v2. Invalid or absent persisted
palette values normalize to Warm without discarding other settings.

Avalonia's non-accent Fluent palette properties do not notify existing brushes.
Changing the palette therefore replaces the configured FluentTheme at its existing
style position and replaces the dynamic surface dictionary. Tests assert actual
realized popup backgrounds, open windows, and restoration of Warm.

## 59. Windows portable and per-user setup packages

A local, git-ignored build.bat delegates to the tracked Windows build script. It builds,
tests, publishes a self-contained managed/untrimmed app, creates a portable ZIP,
compiles NSIS setup, and launches the portable app only after success. Each run
uses a new artifact directory; failed stages stop without overwriting prior output.

Portable packages retain the existing AppData profile. Setup installs binaries in
the current user's LocalAppData Programs folder and doubles as an upgrade package.
Owned-path manifests, hashes, atomic manifest publication and staged uninstall
preserve unknown files, user-replaced shortcuts, profiles and downloads. Setup
refuses running installed processes and leaves browser registration to the app.
Isolated-profile installer execution remains unverified. Ordinary CLR shipping remains the
default.
