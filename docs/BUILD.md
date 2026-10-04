# Build and development

Use the .NET 10 SDK selected by global.json. Windows builds include the bundled aria2 executable; Linux needs aria2 from its package manager, and macOS can use `brew install aria2`. An alternative executable can be selected in Settings → Advanced. Node 24 runs version/release checks; Python 3 runs translation validation.

Run from the repository root:

```sh
dotnet build Colibri.slnx
dotnet test Colibri.slnx
python scripts/validate-translations.py
dotnet run --project src/Colibri.App
dotnet run --project src/Colibri.App -- --minimized
```

UI tests use Avalonia's headless platform; no display server is needed. The optional `tests/Colibri.Engine.Aria2.Smoke` project transfers a real file and is not part of the regular test run. Windows uses a Windows target framework for notifications; Linux/macOS use plain net10.0. The selection is central in Directory.Build.props.

For Windows self-contained installer/ZIP output, run `powershell.exe -NoProfile -File scripts/build-windows.ps1`; it bootstraps pinned NSIS if necessary, builds, tests, publishes and packages serially, then launches the new portable app. The installer, portable ZIP, `release-manifest.json` and `SHA256SUMS` land in `releases/<version>/`; the build stops before building if that folder already exists. Add `-NoLaunch` to avoid launching. [Packaging details](WINDOWS-PACKAGING.md) cover output ownership, prerequisites and data preservation. [Version/release policy](RELEASES.md) explains mandatory commit version increases and hook setup. [Translations](TRANSLATING.md) explains contribution checks.

Source layout: Core holds download logic and contracts; Engine.Aria2 controls aria2 over JSON-RPC; Platform implements OS services; Platform.Ipc is shared by App and NativeHost; App supplies Avalonia UI and storage; NativeHost bridges the browser extension. The current extension lives in the separate `albertgmz/colibri-browser-integration` repository.

Settings/database/logs use the existing per-user Colibri profile (Windows `%LOCALAPPDATA%\Colibri`, Linux/macOS locations chosen by platform paths). Downloads use the configured category/base folders. Secrets captured from browser requests are encrypted at rest and cleared on completion. Packaging does not move or reset this profile. Shell autostart/browser/notification registration is separate OS metadata. See [decisions](DECISIONS.md) and [protocol](protocol.md) for integration details.
