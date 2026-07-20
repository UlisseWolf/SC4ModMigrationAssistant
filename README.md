# SC4 Mod Migration Assistant

A Windows desktop tool for SimCity 4 modders. It scans your `Plugins` folder and flags files
in your `075-my-plugins` and `895-my-overrides` folders that duplicate content already present
elsewhere in `Plugins`, so you can safely move them out of the way.

Built with WPF (.NET 10) and [csDBPF](https://github.com/noah-severyn/csDBPF) for reading SimCity 4's DBPF
file format.

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

## Screenshots

*(add screenshots here)*

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime
  to run a published build
- `csDBPF.dll` — see [Setup](#setup) below

## Setup

1. Clone this repository.
2. Obtain `csDBPF.dll` and place it in the `libs/` folder at the project root, so you have
   `libs/csDBPF.dll`. If it's available as a NuGet package instead, replace the `<Reference>`
   entry in `SC4ModMigrationAssistant.csproj` with a `<PackageReference>`.
3. Build and run:

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

## Configuration

A few constants in `Services/DbpfScanService.cs` can be adjusted:

| Constant | Purpose | Default |
|---|---|---|
| `DbpfExtensions` | File extensions treated as DBPF files | `.dat`, `.sc4lot`, `.sc4model`, `.sc4desc` |
| `Overrides075FolderName` / `Overrides895FolderName` | Override folder names to look for | `075-my-plugins`, `895-my-overrides` |
| `ExcludedTgis` | TGIs excluded from comparison as non-content entries | DBPF Directory record, default LD entry |
| `ProgressReportInterval` | How often the scan progress bar updates | every 100 files |

## Project structure

```
SC4ModMigrationAssistant.csproj
App.xaml / App.xaml.cs
MainWindow.xaml / MainWindow.xaml.cs
LogEntryView.cs
Models/
  SourceCategory.cs
  TgiKey.cs
  ScannedFile.cs
  LogColor.cs
Services/
  DbpfScanService.cs
  DuplicateMoverService.cs
libs/
```

## Contributing

Issues and pull requests are welcome.

## License

*(add a license of your choice, e.g. MIT)*

## Disclaimer

This tool moves files out of your Plugins folder based on automated heuristics. Back up your
Plugins folder before running it, and review the log before moving files if you're unsure.
