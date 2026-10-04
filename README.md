# Colibri

Colibri is a download manager built with C#/.NET 10, Avalonia and aria2. It provides categorized downloads, pause/resume, live progress, detailed transfer views, tray controls and browser capture.

**Status: in active development.** Windows transfers, UI and Chrome capture, including authenticated-cookie downloads, have been tested on Windows. Grouped capture preferences, named scheduled queues, protected HTTP proxy settings, opt-in v1 torrent import and optional configured media tools are available. [Feature boundaries](docs/FEATURES.md) describe supported discovery, segmented media and routing limitations. Linux/macOS desktop UI and credential stores still need runtime verification; strict adapter enforcement is rejected on every platform.

## Download and use

Download the versioned Windows installer or portable ZIP from [GitHub releases](https://github.com/albertgmz/Colibri/releases). The repository automatically packages and publishes tested `main` branch tips.

The installer is per-user and preserves settings and downloads on upgrade/uninstall. The portable ZIP is unpack-and-run and uses the same existing AppData profile. Exit Colibri from its tray menu before upgrading. See [Windows packaging](docs/WINDOWS-PACKAGING.md).

Settings → General checks for updates at startup and hourly, with manual checking and verified downloads. Installed Windows copies can exit and open setup; portable copies receive a ZIP to extract into a new folder. [Update behavior](docs/UPDATES.md) explains the feed and installation checks.

Browser capture for Chrome/Edge is maintained in the separate [browser integration repository](https://github.com/albertgmz/colibri-browser-integration). Install its extension and use Colibri Settings → Browser integration → Install / repair. App language and browser language are configured independently; [translation contributions](docs/TRANSLATING.md) are welcome.

## Build

For Windows packages, with .NET 10, Node 24 and PowerShell installed:

```powershell
powershell.exe -NoProfile -File scripts/build-windows.ps1
```

It obtains pinned NSIS if needed, builds/tests/publishes/packages serially to a new output directory, then launches the portable app. Use `-NoLaunch` for packages only. An existing local `build.bat` wrapper may be used; it is intentionally ignored by Git.

For development on Windows, Linux or macOS:

```sh
dotnet build Colibri.slnx
dotnet test Colibri.slnx
dotnet run --project src/Colibri.App
```

Linux/macOS require aria2 separately. See [build details](docs/BUILD.md) and [version/release policy](docs/RELEASES.md). Every new app repository commit must increase root VERSION; install the local checks with `node scripts/install-hooks.mjs`.

## Documentation and licence

[Features](docs/FEATURES.md) · [Architecture decisions](docs/DECISIONS.md) · [Browser protocol](docs/protocol.md) · [Plugins](docs/plugins.md)

Colibri uses the [MIT License](LICENSE). Windows ships aria2 as a separate GPLv2 program, with its licence and matching source; see [aria2 notes](third_party/aria2/README.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
