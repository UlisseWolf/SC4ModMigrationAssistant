# SC4 Mod Migration Assistant

WPF application (.NET 9, Windows) that finds and moves duplicate files (matched by TGI)
found inside the `075-my-plugins` and `895-my-overrides` folders of a SimCity 4 `Plugins`
folder, using the **csDBPF** library.

## 1. Add the csDBPF.dll reference

This project was built with only the `csDBPF.xml` documentation file available, not the
compiled DLL. Before building:

1. Copy `csDBPF.dll` (built for a compatible target, e.g. net8.0/net9.0) into this
   project's `libs/` folder, so you end up with `libs\csDBPF.dll`.
2. (Optional but recommended) also copy `csDBPF.xml` into `libs/` for IntelliSense.
3. If the DLL depends on other assemblies (e.g. `DBPFSharp.dll` for QFS
   compression, referenced in the XML docs), copy those into `libs/` too and add a
   matching `<Reference>` in `SC4ModMigrationAssistant.csproj` — or, if csDBPF is
   available as a NuGet package, replace the `<Reference>` tag with a regular
   `<PackageReference Include="csDBPF" Version="x.y.z" />`.

If the DLL lives somewhere else, update `HintPath` in the `.csproj`:

```xml
<Reference Include="csDBPF">
  <HintPath>libs\csDBPF.dll</HintPath>
</Reference>
```

## 2. Build & run

```
dotnet build
dotnet run
```

(Requires the .NET 9 SDK with the Windows Desktop workload installed.)

## 4. How it works

1. **Browse...** — select the `Plugins` folder (the one that contains the
   `075-my-plugins` and `895-my-overrides` subfolders).
2. **Scan TGIs**:
   - Recursively scans `Plugins`, **excluding** `075-my-plugins` and
     `895-my-overrides` — every file read (`.dat`, `.sc4lot`, `.sc4model`,
     `.sc4desc`) is logged in **black** with its TGI count.
   - Scans `075-my-plugins` — files logged in **blue**. A file here is flagged as a
     **duplicate** (logged in **red**) if it shares at least one (non-excluded - see
     section 5) TGI with a file in `Plugins`. It is never compared against
     `895-my-overrides`, nor against other files inside `075-my-plugins` itself.
   - Scans `895-my-overrides` — files logged in **blue**. A file here is only flagged as a
     duplicate if **both** conditions hold against `Plugins` specifically: it shares a TGI
     with a `Plugins` file, **and** a file with the same name (case-insensitive) exists in
     `Plugins`. See section 6 for why 895 needs the extra name check, and section 7 for why
     the comparison is scoped only to `Plugins`.
   - The progress bar and the "X / Y files" counter update every 100 files
     processed (see `DbpfScanService.ProgressReportInterval`).
3. **Move Duplicates** — asks for a destination folder and physically moves every
   file flagged as a duplicate there, keeping the relative subfolder structure
   (to avoid name collisions and make it easy to trace a file back to its
   origin). Each move is logged in red; errors (file in use, permissions, etc.)
   are logged in orange and don't stop the remaining moves.

General status/warning messages are logged in gray; files that could not be read
as valid DBPF are logged in orange and skipped.

A **Cancel** button is available while a scan or move is running.
