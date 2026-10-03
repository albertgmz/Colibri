# Third-party notices

Colibri uses the following third-party software. Each component remains under its own licence.

## Bundled and separate programs

| Component | Licence | Notes |
|---|---|---|
| [aria2](https://aria2.github.io/) 1.37.0 | GPLv2 (with an OpenSSL exception) | Shipped as a separate program (`aria2/aria2c.exe` on Windows) and controlled over JSON-RPC; it is not linked into Colibri. Its licence is copied next to it (`aria2/COPYING`); see `third_party/aria2/README.md` for the source code. |

## Artwork

| Component | Licence | Notes |
|---|---|---|
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | MIT, Copyright (c) 2020 Microsoft Corporation | Path data of a few 20 px "regular" icons, in `src/Colibri.App/Resources/Icons.axaml`. |
| [Inter](https://rsms.me/inter/) typeface | SIL Open Font License 1.1 | Embedded through the `Avalonia.Fonts.Inter` package. |

## NuGet packages used by the application

| Package | Version | Licence |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter (and the Avalonia platform packages they bring in) | 12.1.3 | MIT |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT |
| Avalonia.Angle.Windows.Natives (dependency of Avalonia on Windows) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE project) |
| SkiaSharp, HarfBuzzSharp (dependencies of Avalonia) | 3.119.4, 8.3.1.3 | MIT |
| MicroCom.Runtime, Tmds.DBus.Protocol (dependencies of Avalonia) | 0.11.6, 0.94.1 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Extensions.Hosting (and the Microsoft.Extensions.* packages it brings in) | 10.0.12 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | MIT |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | Apache-2.0 |
| SQLite (native library inside SQLitePCLRaw.lib.e_sqlite3) | | Public domain |
| Serilog | 4.3.0 | Apache-2.0 |
| Serilog.Extensions.Hosting, Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 |

## NuGet packages used only by the tests

| Package | Version | Licence |
|---|---|---|
| xunit.v3 | 3.2.2 | Apache-2.0 |
| Avalonia.Headless.XUnit | 12.1.3 | MIT |
