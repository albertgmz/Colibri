# Colibri v1 plan

Colibri is a cross-platform download manager (Windows, Linux, macOS) built with
C# / .NET 10 / Avalonia 12, driving `aria2c` over JSON-RPC.

## Pinned toolchain (checked on NuGet, October 2026)

| Package | Version | Licence |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Headless.XUnit | 12.1.3 | MIT |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Extensions.Hosting | 10.0.12 | MIT |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| Serilog.Extensions.Hosting / Serilog.Sinks.File | 10.0.0 / 7.0.0 | Apache-2.0 |
| xunit.v3 (version compatible with Avalonia.Headless.XUnit) | 3.2.x | Apache-2.0 |
| aria2 (separate process, not linked) | 1.37.0 | GPLv2 |

Versions are pinned centrally in `Directory.Packages.props`.

## Projects

```
src/Colibri.Core            domain + abstractions (no UI, no aria2, no OS code)
src/Colibri.Engine.Aria2    aria2 process host + JSON-RPC client -> IDownloadEngine
src/Colibri.Platform        per-OS implementations of the Core platform interfaces
src/Colibri.Platform.Ipc    per-OS pipe/mutex names and foreground handoff, shared with the native host
src/Colibri.App             Avalonia app (views, view models, storage, IPC, hosting)
src/Colibri.NativeHost      native-messaging host <-> local pipe to the app
extension/                  MV3 extension for Chrome and Edge
tests/*                     unit tests + headless UI smoke tests
```

## Core contracts (shape, not final code)

- `DownloadItem`: Id (Guid), Url, FinalUrl?, FileName, SaveFolder, Category,
  State, TotalBytes?, CompletedBytes, DownloadSpeed, Connections, EngineId,
  EngineHandle? (aria2 GID), Referrer?, UserAgent?, Headers, AddedAt,
  CompletedAt?, ErrorMessage?. The database row is the truth; the engine handle
  is only a pointer into an engine.
- `DownloadState`: Queued, Active, Paused, Completed, Failed. Allowed transitions
  live in one place (`DownloadStateMachine`) and are unit tested.
- `DownloadRequest`: Uri, SuggestedFileName?, Headers, Referrer?, UserAgent?,
  Cookies?, Size?, MimeType?.
- `LinkContext`: Referrer?, Cookies?, Headers, UserAgent?, FileName?, Size?, MimeType?.
- `ILinkResolver`: `Priority`, `ResolveAsync(Uri, LinkContext, CancellationToken)`
  returning `null` (not mine) or a list of `DownloadRequest`. `LinkResolverPipeline`
  asks resolvers in priority order; first non-null answer wins.
  v1 ships `DirectLinkResolver` (pass-through, lowest priority).
- `IDownloadEngine`: Id, CanHandle(request), StartAsync/StopAsync, AddAsync
  (returns engine handle), PauseAsync, ResumeAsync, RemoveAsync, GetStatusAsync,
  GetActiveAsync, and an event stream of status snapshots / lifecycle events.
- `IDownloadRepository`: async CRUD over `DownloadItem` (SQLite impl in App).
- `ISettingsStore` + `AppSettings` (JSON impl in App).
- `DownloadManager`: the use-case service. Incoming link -> URL policy ->
  resolvers -> sanitize file name -> pick category/folder -> engine -> repository.
  Also pause/resume/delete, reconcile on startup, apply engine snapshots.
- Pure helpers: `FileNameSanitizer`, `CategoryMapper`, `UrlPolicy`.
- Platform interfaces: `INotificationService`, `IAutostartService`,
  `IBrowserHostRegistrar`, `IShellService`, `ITaskbarProgress`,
  `IAria2Locator`, `IAppPaths`, `IIpcEndpointProvider`, `IForegroundHandoff`.

## Milestones (step -> verify)

1. **Skeleton.** Solution, Core models and abstractions, Core unit tests, CI
   workflow for windows/ubuntu/macos.
   -> verify: `dotnet build` + `dotnet test` green locally; CI green after push.
2. **aria2 engine.** Process host (random port, random secret,
   `--stop-with-process`, session file, restart on crash) and JSON-RPC client
   over WebSocket; tests against a fake transport; console smoke test.
   -> verify: smoke test downloads a real file, pauses it, resumes it, completes.
3. **App shell.** Main window (nav pane, command bar, virtualized table, status
   bar), Add URL window, SQLite + JSON settings, persistence across restarts.
   -> verify: run app, add URL, see live progress, restart, row still there;
   headless smoke tests green.
4. **Features + platform.** Details pane with segment bar, tray behaviour,
   single instance over the local pipe, notifications, settings page, per-OS
   implementations.
   -> verify: run on Windows, exercise each feature; tests green; CI green.
5. **Browser capture.** Native host, MV3 extension, registration via
   `IBrowserHostRegistrar`.
   -> verify: install extension unpacked in Chrome and Edge, capture a download,
   capture with the app closed, fallback when the host is unreachable.
6. **Polish + README.**
   -> verify: full manual checklist on Windows; CI green.

Each milestone: build, run tests, commit, push branch `v1`.
