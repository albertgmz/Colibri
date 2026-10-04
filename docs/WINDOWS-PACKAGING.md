# Windows packages

Run from the repository on Windows with the .NET 10 SDK and Node 24:

```powershell
powershell.exe -NoProfile -File .\scripts\build-windows.ps1
```

The local root `build.bat` is deliberately ignored by Git. Its tracked implementation is `scripts/build-windows.ps1`; after cloning, invoke that script directly or recreate the local wrapper:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1 -NoLaunch -NsisPath C:\Tools\nsis-3.13\makensis.exe
```

Local wrapper contents:

```bat
@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-windows.ps1" %*
exit /b %errorlevel%
```

`-NsisPath` may also come from `COLIBRI_NSIS_PATH`. If neither is supplied, the script uses `artifacts/tools/nsis-3.13/makensis.exe` or downloads the official NSIS 3.13 ZIP through HTTPS SourceForge mirrors. It accepts only SHA256 `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`, validates archive paths, and extracts a new tool directory. This pin was taken from an inspected official download; it is not an upstream published checksum. It never runs an NSIS installer or replaces an existing tool directory. A failed download/hash/extraction stops the build; retained staging files can be inspected manually. For offline builds, supply a complete extracted distribution explicitly. Keep the compiler beside its NSIS includes, plugins, and other distribution files.

Before creating output or starting a build, the script imports Utility and Archive from their absolute manifests under the current host's `$PSHOME/Modules` and resolves `Get-FileHash` and `Compress-Archive`. This prevents a batch file launched from PowerShell 7 from making Windows PowerShell 5.1 load incompatible PowerShell 7 modules through an inherited `PSModulePath`; the script does not change that environment variable. Missing packaging commands fail at this preflight. Hashing and ZIP creation invoke those resolved commands directly.

The script serially builds the solution, runs its tests, and publishes a self-contained `win-x64` application with trimming, Native AOT, and ReadyToRun disabled. It then creates the portable ZIP and compiles the installer. Any failed stage stops the script. Each invocation owns a fresh `artifacts/windows-<timestamp>-<random suffix>/` working directory. Finished release files move to `releases/<version>/` only after every stage succeeds. If `releases/<version>/` already exists, the script stops before building and never replaces it; bump `VERSION` or move the old folder away. Earlier packages and running published applications are never overwritten. A running development copy under `src` blocks the shared build. Locked files cause an immediate failure without retries or process termination.

Successful output in `releases/<version>/`:

- `Colibri-<version>-win-x64-portable.zip`: the portable folder below.
- `Colibri-<version>-win-x64-setup.exe`: per-user installation and upgrade package.
- `release-manifest.json` and `SHA256SUMS`: exact release filenames, byte lengths and SHA256 hashes.

The working directory under `artifacts/` keeps:

- `Colibri/`: executable, runtime, native messaging host, aria2, dependencies, licenses, notices, and a SHA256 package manifest. Debug symbols are excluded.
- `build-results.json`: version and absolute output paths.

The default command launches the new portable application only after all packages succeed. `-NoLaunch` produces the same artifacts without launching an application. The script never launches the installer.

Root VERSION stamps the app, native host and setup; packaging refuses mismatched executable versions. [Release policy](RELEASES.md) covers the mandatory version increase on every app repository commit and CI publishing.

## Portable data boundary

Portable means unpack the folder and run `Colibri.exe`. Colibri still uses its existing `%LOCALAPPDATA%\Colibri` profile for settings, database, and logs; downloads use the configured download folder. This package does not introduce a removable-drive data mode. Preserve the entire portable folder so its native host, aria2, and dependencies stay adjacent to the application.

## Install, upgrade, and uninstall

Setup installs for the current user at `%LOCALAPPDATA%\Programs\Colibri`. It creates Start Menu shortcuts, offers an optional desktop shortcut, and records uninstall information under HKCU. Rerun a newer setup package to upgrade at the same location. Exit the installed application from its tray menu first: setup refuses a running installed application, native host, or download engine and never kills them. When setup stops, the reason is appended to `%TEMP%\Colibri-setup.log`.

The installer records exact owned paths in `installed-files.json`. Payload hashes are checked before mutation. Existing unowned collisions, symbolic links or junctions in the install path, inaccessible processes, and locked owned files stop installation. During an upgrade, both file generations are recorded before copying so a failed copy remains repairable by rerunning setup. There is no atomic rollback guarantee.

Ownership manifests are validated and flushed to unique temporary files in the same directory, then atomically published. Failed publication preserves the previous valid manifest. Shortcut ownership is recorded separately by exact known location, and a recorded shortcut still counts as owned only while it targets the installed `Colibri.exe` or `Uninstall.exe` without arguments. Shortcuts are not compared by bytes because Explorer rewrites them after creation. Pre-existing unowned or retargeted shortcuts block setup, and uninstall preserves shortcuts the user retargeted. Shortcut ancestors also reject junctions and symbolic links. Uninstall retains its original executable and ownership manifests until external shortcut and registry cleanup succeeds, so cleanup failures can be retried.

Uninstall removes only manifest-owned binaries and empty known directories, plus the explicit setup shortcuts and HKCU uninstall metadata. Unknown files are preserved. Installation, upgrade, and uninstall preserve `%LOCALAPPDATA%\Colibri` and downloads. Neither setup nor uninstall registers or removes browser native messaging hosts; use the application's Settings **Install / repair** action for browser registration after changing the executable location.

## Compiler and review

[NSIS 3.13](https://nsis.sourceforge.io/Download), released September 27, 2026, provides a portable Windows compiler under [permissive licenses](https://nsis.sourceforge.io/License). Installer commands follow the official [NSIS command reference](https://nsis.sourceforge.io/Docs/Chapter4.html) and [preprocessor reference](https://nsis.sourceforge.io/Docs/Chapter5.html). This pipeline does not sign the packages.

Before distributing, run the build with `-NoLaunch`, inspect the manifest and ZIP, and verify a clean per-user install, a running-app refusal, upgrade, and uninstall in an isolated Windows profile. Include an unowned file to verify its preservation, and verify that existing settings and downloaded files survive all three operations. These checks require the resulting executable installer; source review alone does not confirm installer behavior.

Dependency-free helper checks use synthetic files under a guarded temporary directory and never run those files:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-windows-packaging.ps1
```

They cover shortcut collisions, user replacements and Explorer-rewritten shortcuts, the failure log, a helper started with an inherited PowerShell 7 module path, failed atomic manifest publication, locked-shortcut retry state, staged uninstall finalization, and unknown-file preservation. NSIS registry behavior and actual installed process refusal still require isolated installer verification.
