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
logged and left alone. While Colibri shows a download as paused, an "active" report from aria2 is
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

## 21. Temporary `IShellService`

Until the per-OS implementations exist, `ProcessShellService` opens files and folders through
`Process.Start` with `UseShellExecute`. "Show in folder" opens the containing folder without
selecting the file.

## 22. Engine add takes an optional handle and a paused flag

`IDownloadEngine.AddAsync` accepts an existing handle and a "start paused" flag. Reconcile and retry
add a download again under its old GID (so the handle in the database stays valid and aria2 finds its
control file) and can restore a paused download without it starting first.
