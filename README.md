# SC4 Mod Migration Assistant

[![CI](https://github.com/UlisseWolf/SC4ModMigrationAssistant/actions/workflows/ci.yml/badge.svg)](https://github.com/UlisseWolf/SC4ModMigrationAssistant/actions/workflows/ci.yml)
[![Release](https://github.com/UlisseWolf/SC4ModMigrationAssistant/actions/workflows/release.yml/badge.svg)](https://github.com/UlisseWolf/SC4ModMigrationAssistant/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/UlisseWolf/SC4ModMigrationAssistant)](https://github.com/UlisseWolf/SC4ModMigrationAssistant/releases/latest)
[![License: MIT](https://img.shields.io/github/license/UlisseWolf/SC4ModMigrationAssistant)](LICENSE.md)

A cross-platform desktop tool (Windows, Linux, macOS) for SimCity 4 modders. It scans your
`Plugins` folder and flags files in your `075-my-plugins` and `895-my-overrides` folders that
duplicate content already present elsewhere in `Plugins`, so you can safely move them out of
the way.

Built with [Avalonia UI](https://avaloniaui.net/) (.NET 10).

*The tool was developed with Claude's support and under human supervision*

## Download

Prebuilt, self-contained binaries for Windows, Linux, and macOS (Apple Silicon) are published
automatically on the [Releases page](https://github.com/UlisseWolf/SC4ModMigrationAssistant/releases) —
no .NET runtime install required. See [Setup](#setup) if you'd rather build from source.

## Cross-platform support

This app runs natively on Windows, Linux, and macOS via Avalonia UI. Two things made this a
clean port:

- The DBPF/TGI parsing (`Services/DbpfParsingService.cs`, `Services/DbpfFileSystemService.cs`)
  is plain, portable C# reading raw file bytes directly - no external native or Windows-only
  library dependency at all.
- `Models/` and `Services/` (all scanning, comparison, moving, and sc4pac catalog logic) never
  referenced the UI framework, so none of it needed to change for this port - only the UI layer
  (`MainWindow`, dialogs, file/folder pickers) did.

A few platform-specific notes:

- **Folder/file pickers** use Avalonia's `IStorageProvider`, which uses each OS's native
  picker - including the XDG desktop portal on Linux.
- **Dark title bar**: the Windows-only `DwmSetWindowAttribute` call that colors the native
  title bar is guarded by `OperatingSystem.IsWindows()` and does nothing on Linux/macOS, where
  the window chrome already follows the app's dark theme through Avalonia itself.
- **Opening a mod's page** (`Open Page` button) uses `Process.Start(..., UseShellExecute = true)`,
  which resolves to the OS's default URL handler on every platform (`xdg-open` on Linux, `open`
  on macOS, the default browser association on Windows).

## Features

- Recursively scans a Plugins folder for `.dat`, `.sc4lot`, `.sc4model`, and `.sc4desc` files
- Detects duplicate content by comparing TGI (Type-Group-Instance) identifiers
- Automatically excludes non-content bookkeeping TGI entries (e.g. the DBPF directory record)
  that would otherwise cause false positives
- Extra file-name check for `895-my-overrides`, since that folder is meant to hold intentional
  overrides that share a TGI with the original file by design
- Low memory footprint, suitable for machines with as little as 4–8 GB of RAM
- Live, color-coded log and progress bars for both the scan and the comparison step
- Moves (never deletes) duplicates to a folder of your choice, preserving the original
  subfolder structure
- Cancel support for long-running scans
- **Check sc4pac Catalog**: scans `075-my-plugins` / `895-my-overrides`, matches their TGIs
  against the community-run [SC4 Prop Texture Catalog](https://github.com/noah-severyn/SC4PropTextureCatalog),
  and lists any [sc4pac](https://sc4pac.github.io/) package that already provides that content —
  so you can install it properly instead of keeping a loose manual copy
- Cross-checks results against sc4pac's own `sc4pac-plugins.json` / `sc4pac-plugins-lock.json`
  (auto-detected from the default profile location), so packages you've already installed —
  explicitly or as a dependency — and the false positives that come from two different mods
  sharing the same TGI, are filtered out automatically
- **Export sc4pac JSON**: saves every package found by the catalog check to a
  `{ "explicit": [...] }` file, the same format sc4pac itself uses, ready to hand off to sc4pac

## Screenshots

![scanning](https://www.simtropolis.com/objects/screens/monthly_2026_07/A2.png.34f45269b6413555afef6de871bdf61b.png "scanning plugins folder")

![sc4pac](https://www.simtropolis.com/objects/screens/monthly_2026_07/A1.jpg.cdbadc0167a7e636e06fe7fae8243e81.jpg "find sc4pac")

## Requirements

- Windows 10/11, Linux, or macOS
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime
  to run a published build — no platform-specific workload needed
- Internet access the first time you use **Check sc4pac Catalog** (to download `Catalog.db`,
  ~22 MB); the scan/move features work fully offline

## Setup

Prebuilt binaries are available from the [Releases page](https://github.com/UlisseWolf/SC4ModMigrationAssistant/releases) —
most users won't need to build from source at all. To build it yourself instead:

1. Clone this repository.
2. Build and run:

   ```bash
   dotnet build
   dotnet run
   ```

To publish a self-contained build for a specific OS:

```bash
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained      # osx-arm64 for Apple Silicon
```

## Usage

1. **Browse...** – select your SimCity 4 `Plugins` folder (the one containing
   `075-my-plugins` and `895-my-overrides`).
2. **sc4pac-plugins.json** – auto-detected on startup from sc4pac's default profile location
   (`%AppData%\io.github.memo33\sc4pac\config\profiles\...`); use **Browse...** next to it if
   sc4pac isn't installed there, uses a different profile, or wasn't detected. This is optional,
   but strongly recommended before running **Check sc4pac Catalog** (step 5) — see
   [Filtering out already-installed packages](#filtering-out-already-installed-packages-and-cross-mod-tgi-false-positives).
3. **Scan TGIs** – scans the whole folder tree and identifies duplicates. Files are
   color-coded by origin, and detected duplicates are highlighted.
4. **Move Duplicates** – choose a destination folder; all detected duplicates are moved there
   (with their relative subfolder structure preserved), freeing them from `Plugins` without
   permanently deleting anything.
5. **Check sc4pac Catalog** – scans only `075-my-plugins` / `895-my-overrides` and checks their
   TGIs against the SC4 Prop Texture Catalog (downloaded automatically on first use, then cached
   locally). Any matching sc4pac package is listed with:
   - **Copy ID** – copies the sc4pac package identifier (`group:package-name`)
   - **Copy sc4pac add** – copies the ready-to-run `sc4pac add group:package-name` command
   - **Open Page** – opens the mod's actual download/info page in your browser, when known

   sc4pac itself doesn't expose a documented way to trigger an install from an external link, so
   installing is a two-step, always-accurate process: copy the ID (or the command) here, then
   paste it into sc4pac's own *Find Packages* search (or run the copied command in a terminal).

   **Export sc4pac JSON** (button above the results list) saves every package found so far to a
   `{ "explicit": ["group:package-id", ...] }` file — the same format sc4pac itself uses for its
   explicit-packages list — so you can hand it straight to sc4pac and install everything found
   in one go, instead of adding packages one by one.

## How duplicate detection works

A file inside `075-my-plugins` is flagged as a duplicate if it shares at least one TGI with a
file in the main `Plugins` folder.

A file inside `895-my-overrides` is flagged as a duplicate only if **both** are true:

- it shares at least one TGI with a file in `Plugins`, **and**
- a file with the same name already exists in `Plugins`.

This extra check exists because `895-my-overrides` is meant to hold intentional overrides,
which by design share a TGI with the file they replace — a TGI match alone there is expected,
not a mistake.

Duplicates are only ever compared against the main `Plugins` folder; files within
`075-my-plugins` and `895-my-overrides` are never compared against each other.

## How the sc4pac catalog check works

`Catalog.db` is a SQLite database built and maintained by the
[SC4 Prop Texture Catalog](https://github.com/noah-severyn/SC4PropTextureCatalog) project. It's
downloaded directly from GitHub on first use and cached at
`%LocalAppData%\SC4ModMigrationAssistant\Catalog.db` (delete that file to force a fresh
download). It maps individual TGIs to the file they came from, and that file to the sc4pac
package(s) that ship it.

For every distinct TGI found in `075-my-plugins` / `895-my-overrides`, the app looks up whether
that exact TGI (or, for entries where the catalog only records Group+Instance, that Group and
Instance) belongs to a known package, then groups the results by package so you see one entry
per mod rather than one per file.

There's no publicly documented way to make an external link launch an install directly inside
sc4pac, so results are given as a package ID / CLI command you paste into sc4pac (see
[Usage](#usage)) plus, when available, a link to the mod's real page — not a fabricated
"click to install" link that might not actually work.

### Filtering out already-installed packages and cross-mod TGI false positives

Two things can make the catalog check suggest a package you don't actually need:

1. **You already installed it via sc4pac** — either explicitly, or as an automatic dependency
   of something else (e.g. `nam:bridges`, pulled in as a dependency of NAM). The local file in
   `075`/`895` still exists (maybe left over from before you switched to sc4pac), so its TGI
   still matches the catalog, even though sc4pac is already managing that content.
2. **A TGI is shared by more than one catalog package.** Some resources (shared dependencies,
   assets reused across packs) are indexed under more than one package in the catalog, so a
   single local file can resolve to several candidates - only one of which may actually be
   relevant.

To avoid both, the app reads sc4pac's own profile files (see
[sc4pac's documentation](https://memo33.github.io/sc4pac/#/), "Details" section) - both are
looked for in the same folder, whichever one is selected/detected:

- `sc4pac-plugins.json`'s `"explicit"` array — packages you added yourself.
- `sc4pac-plugins-lock.json`'s `"installed"` array — **every** package actually installed,
  dependencies included. This is the one that matters most: `sc4pac-plugins.json` alone would
  have missed `nam:bridges` in the example above, since it only tracks explicit additions.

Both lists are combined, and for every TGI, **all** of its candidate packages are skipped if
**any** of them is in that combined list. This handles case 1 directly (now covering
dependency-only packages too), and also resolves most instances of case 2: if the TGI's
"correct" package is already installed, the other candidate(s) that merely happen to share the
same TGI are false positives anyway and get filtered out along with it.

`sc4pac-plugins.json` is auto-detected on startup from sc4pac's default location
(`%AppData%\io.github.memo33\sc4pac\config\profiles\...\sc4pac-plugins.json`, picking the most
recently modified one if you have several profiles); `sc4pac-plugins-lock.json` is then looked
for automatically right next to it. Use **Browse...** in the UI to point at either file manually
if auto-detection doesn't find them (e.g. a non-default profile location) - whichever one you
pick, the app still looks for its sibling in the same folder. If the lock file can't be found,
the catalog check still runs, but the log will warn that dependency-only packages may still be
suggested.

## Configuration

A few constants in `Services/DbpfScanService.cs` can be adjusted:

| Constant | Purpose | Default |
|---|---|---|
| `DbpfExtensions` | File extensions treated as DBPF files | `.dat`, `.sc4lot`, `.sc4model`, `.sc4desc` |
| `Overrides075FolderName` / `Overrides895FolderName` | Override folder names to look for | `075-my-plugins`, `895-my-overrides` |
| `ExcludedTgis` | TGIs excluded from comparison as non-content entries | DBPF Directory record, default LD entry |
| `ProgressReportInterval` | How often the scan progress bar updates | every 100 files |

The catalog download URL and local cache path are in
`Services/CatalogDatabaseService.cs` (`CatalogDownloadUrl`, `CacheFilePath`).

## Project structure

```
.github/workflows/
  ci.yml                   Build check on every pull request (Windows/Linux/macOS)
  release.yml              Packages and publishes a GitHub Release on push to master or a v*.*.* tag
SC4ModMigrationAssistant.csproj
LICENSE.md
Program.cs                Avalonia entry point (BuildAvaloniaApp / Main)
App.axaml / App.axaml.cs  FluentTheme (Dark) + Synthwave accent/brushes + control styles
MainWindow.axaml / MainWindow.axaml.cs
LogEntryView.cs
Dialogs/
  MessageDialog.axaml / .axaml.cs   Custom OK / Yes-No modal (Avalonia has no built-in MessageBox)
Models/
  SourceCategory.cs
  TgiKey.cs
  ScannedFile.cs
  CatalogScanFile.cs
  Sc4pacMatch.cs
  Sc4pacExplicitPackages.cs
  LogColor.cs
Services/
  DbpfFileSystemService.cs
  DbpfParsingService.cs
  DbpfScanService.cs
  DuplicateMoverService.cs
  CatalogDatabaseService.cs
  Sc4pacLookupService.cs
  Sc4pacPluginsFileService.cs
```

## Contributing

Issues and pull requests are welcome.

## License

This project is licensed under the [MIT License](LICENSE.md)

## Disclaimer

This tool moves files out of your Plugins folder based on automated heuristics. Back up your
Plugins folder before running it, and review the log before moving files if you're unsure.
