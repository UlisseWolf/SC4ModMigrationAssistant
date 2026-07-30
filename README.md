# SC4 Mod Migration Assistant

A Windows desktop tool for SimCity 4 modders. It scans your `Plugins` folder and flags files
in your `075-my-plugins` and `895-my-overrides` folders that duplicate content already present
elsewhere in `Plugins`, so you can safely move them out of the way.

Built with WPF (.NET 10).

*The tool was developed with Claude's support and under human supervision*

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

## Screenshots

![scanning](https://www.simtropolis.com/objects/screens/monthly_2026_07/A2.png.34f45269b6413555afef6de871bdf61b.png "scanning plugins folder")

![sc4pac](https://www.simtropolis.com/objects/screens/monthly_2026_07/A1.jpg.cdbadc0167a7e636e06fe7fae8243e81.jpg "find sc4pac")

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime
  to run a published build
- Internet access the first time you use **Check sc4pac Catalog** (to download `Catalog.db`,
  ~22 MB); the scan/move features work fully offline

## Setup

1. Clone this repository.
2. Build and run:

   ```bash
   dotnet build
   dotnet run
   ```

## Usage

1. **Browse...** – select your SimCity 4 `Plugins` folder (the one containing
   `075-my-plugins` and `895-my-overrides`).
2. **Scan TGIs** – scans the whole folder tree and identifies duplicates. Files are
   color-coded by origin, and detected duplicates are highlighted.
3. **Move Duplicates** – choose a destination folder; all detected duplicates are moved there
   (with their relative subfolder structure preserved), freeing them from `Plugins` without
   permanently deleting anything.
4. **Check sc4pac Catalog** – scans only `075-my-plugins` / `895-my-overrides` and checks their
   TGIs against the SC4 Prop Texture Catalog (downloaded automatically on first use, then cached
   locally). Any matching sc4pac package is listed with:
   - **Copy ID** – copies the sc4pac package identifier (`group:package-name`)
   - **Copy sc4pac add** – copies the ready-to-run `sc4pac add group:package-name` command
   - **Open Page** – opens the mod's actual download/info page in your browser, when known

   sc4pac itself doesn't expose a documented way to trigger an install from an external link, so
   installing is a two-step, always-accurate process: copy the ID (or the command) here, then
   paste it into sc4pac's own *Find Packages* search (or run the copied command in a terminal).

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
SC4ModMigrationAssistant.csproj
LICENSE
App.xaml / App.xaml.cs
MainWindow.xaml / MainWindow.xaml.cs
LogEntryView.cs
Models/
  SourceCategory.cs
  TgiKey.cs
  ScannedFile.cs
  CatalogScanFile.cs
  Sc4pacMatch.cs
  LogColor.cs
Services/
  DbpfScanService.cs
  DuplicateMoverService.cs
  CatalogDatabaseService.cs
  Sc4pacLookupService.cs
```

## Contributing

Issues and pull requests are welcome.

## License

This project is licensed under the [MIT License](LICENSE)

## Disclaimer

This tool moves files out of your Plugins folder based on automated heuristics. Back up your
Plugins folder before running it, and review the log before moving files if you're unsure.
