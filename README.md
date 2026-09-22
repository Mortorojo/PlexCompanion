# PlexCompanion

A small tool for **Plex for Windows** that lets you hide Plex's title bar with one key press — and bring it back just as easily.

## What it does

- **One keypress toggles the title bar off and on** (default: `Alt+Q`). You can set any combo you like, including three-modifier keys like `Ctrl+Shift+A`.
- **Dark or light title bar** — matches your taste, applied automatically when Plex is running.
- **Picks up Plex when it starts** — no need to launch anything first. Works after reboots too.
- **Installs to your user folder** (no admin password) — or to Program Files if you prefer (one UAC prompt for that).
- **Uninstalls completely** — removes everything it created.
- **One file to share.** No installer, no runtime, no dependencies.

## Getting started

1. Run **`PlexCompanion.exe`**
2. In the setup window, check that the **Plex location** path looks right (it usually auto-detects correctly). Optionally change the **toggle hotkey** — click the box, press your desired key combo, then click **Set**.
3. Pick **Dark** or **Light** for the title bar appearance.
4. Click **Next**, confirm the install folder (default is fine), and click **Install**.

That's it. Use your hotkey (`Alt+Q` by default) whenever Plex is open to toggle the title bar.

## Common tasks

| Task | How |
|---|---|
| Change settings (hotkey, path, appearance) | Run `PlexCompanion.exe /setup` |
| Uninstall completely | Run `PlexCompanion.exe /remove`, or find "PlexCompanion" in **Settings → Apps** and uninstall |

## Building from source

The whole app is **one C# file** (`src/PlexCompanion.cs`, ~1,540 lines). It uses only the .NET Framework that ships with Windows — no NuGet packages, no build system.

**To compile:**

Double-click **`build.bat`** (or run `build.ps1` in PowerShell). The output appears at `dist\PlexCompanion.exe`.

That's the entire build. If `build.bat` says `csc.exe not found`, you're missing the .NET Framework compiler — install the [Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/) (check the ".NET desktop development" workload) and try again.

### Repository layout

```
src/
  PlexCompanion.cs    ← the entire app (watcher, setup wizard, dialogs)
  PlexCompanion.ico   ← the app icon
build.bat             ← double-click to compile
build.ps1             ← same, for PowerShell
dist/
  PlexCompanion.exe   ← build output (generated, not committed)
LICENSE               ← MIT
```

## Where it stores settings

All under your user's registry (`HKEY_CURRENT_USER\Software\PlexCompanion`):

| Key | What it holds |
|---|---|
| `PlexPath` | Path to your `Plex.exe` |
| `Appearance` | `dark` or `light` |
| `Hotkey` | The modifier + key you chose |
| `InstallPath` | Where the app is installed |

It also writes a **startup entry** (`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\PlexCompanionWatcher`) so it runs when you sign in, and an **Apps & Features entry** so it shows up in the Windows uninstall list. All of these are removed on uninstall.

## How it works (if you're curious)

- A small background process watches for a Plex window. When one appears, it applies the title-bar style (dark or light) and registers your hotkey.
- Toggling the title bar is done by changing Plex's window style flags (removing the caption bar) and resizing to the client area. The width never changes — only the caption height is removed. Restoring puts everything back.
- The hotkey uses Windows' `RegisterHotKey` — the same mechanism Discord, Steam, and most overlay tools use. It doesn't hook your keyboard, inject anything into Plex, or read its memory.
- The process is a single .NET Framework executable. It writes no files at runtime except its own log (in the install folder) and the settings above.

## License

[MIT](LICENSE)
