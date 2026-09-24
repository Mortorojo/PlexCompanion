# Building PlexCompanion

Everything you need to compile the app lives in this folder. There's no build
system, no NuGet packages, no runtime to install — just the .NET Framework
C# compiler that ships with Windows.

## Requirements

- **Windows** with the .NET Framework 4.x C# compiler (`csc.exe`), which is
  present by default at:

  ```
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  ```

  If it isn't there, install the [.NET Framework Developer Pack](https://aka.ms/dotnet-framework-article)
  or the [Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/)
  (".NET desktop development" workload) and try again.

## How to build

Run either script — whichever you prefer. Both compile
`src\PlexCompanion.cs` (one level up) into `dist\PlexCompanion.exe`:

- **`build.bat`** — double-click it, or run it from any terminal
- **`build.ps1`** — run with PowerShell: `powershell -File build.ps1`

Both scripts find the compiler at its default location, compile, and write
the finished executable to **`dist\PlexCompanion.exe`** at the repository root.

## Repository layout (for building)

```
build/
  build.bat         ← double-click to compile
  build.ps1         ← same, for PowerShell
  README.md         ← this file
src/                ← one level up from this folder
  PlexCompanion.cs  ← the entire app (watcher, setup wizard, dialogs)
  PlexCompanion.ico ← the app icon
dist/               ← build output (generated, not committed)
  PlexCompanion.exe
```

> The source (`src/`) and output (`dist/`) live at the repository root; the
> scripts in this folder reference them one level up.

## Notes

- The build is fully deterministic and offline — no network access is needed.
- The version string is set in `src/PlexCompanion.cs` (`Cfg.VERSION`) and in
  the file's header comment; update both to the same value at release time.
