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
