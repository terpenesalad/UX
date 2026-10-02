# Horizon — an ultra-wide desktop for Windows

Horizon turns a wide monitor (or a projected PC screen) into three zones:

```
┌──────────────┬────────────────────────────┬──────────────┐
│ ▌            │                            │            ▐ │
│ ▌ PERIPHERY  │           FOCUS            │  PERIPHERY ▐ │
│ ▌ parked     │   the window you work in   │  parked    ▐ │
│ ▌ windows    │                            │  windows   ▐ │
│ ▌            │                            │            ▐ │
└──────────────┴────────────────────────────┴──────────────┘
  ▲ stash edge                                  stash edge ▲
```

- **Drop a window in a side zone** → it's *parked*: shrunk and stacked in that column, still live, so you can glance at it.
- **Drop it right at the screen edge** → it's *stashed*: minimised and replaced by a small widget with its icon. Music apps also get a play/pause button.
- **Drop a parked window back in the middle**, or click a stash widget → it returns at its original size, centred in focus.
- While you drag, a neon map of the zones fades in so you can see where the window will land.

It's based on the ultra-wide prototypes in Scott Jenson's talk *"Are we really going to use the same Desktop UX forever?"* (Akademy 2026).

> **Status: early prototype.** It moves real windows, so expect rough edges. "Bring every window back" in the tray menu (or quitting Horizon) puts everything back where it was.

---

## 1. Get the .exe (no coding needed)

1. Create a new repository on GitHub and upload this whole folder to it (keep the `.github` folder; it holds the build instructions).
2. Open the repo's **Actions** tab. A run called **Build Horizon** starts by itself (if it doesn't, pick it and click **Run workflow**).
3. When it's green (≈2–3 min), open the run and download **Horizon-win-x64** under *Artifacts*.
4. Unzip it and run `Horizon.exe`. No installer, and you don't need .NET.

To get a proper download page, create a tag such as `v0.1.0` (Releases → *Draft a new release* → new tag). The workflow attaches `Horizon-win-x64.zip` to that release.

**"Windows protected your PC"?** The exe isn't code-signed (signing certificates cost money), so SmartScreen warns you. Click **More info → Run anyway**.

**Start with Windows:** press `Win+R`, type `shell:startup`, and put a shortcut to `Horizon.exe` in that folder.

## 2. Use it

| Do this | What happens |
|---|---|
| Drag a window into a side zone | Parks it in that column |
| Drag a window to the very left/right edge | Stashes it as a widget |
| Drag a parked window into the middle | Restores it to focus |
| Click a stash widget's icon | Restores it to focus |
| `Win + Alt + ←` / `→` | Park the active window left / right |
| `Win + Alt + ↑` | Bring the active window to focus (centres it) |
| `Win + Alt + ↓` | Stash the active window |
| `Win + Alt + P` | Pause / resume Horizon |
| Double-click the tray icon | Show the zone map for 2 seconds |

**Recommended Windows settings** (Settings → System → Multitasking): turn off *"Show snap layouts when I drag a window to the top of my screen"* and *"When I snap a window, suggest what I can snap next to it"*. Otherwise Windows' own snapping fights Horizon at the screen edges.

## 3. The neon grid wallpaper

The `wallpaper` folder has two versions. Both use the same 50% focus zone as the app, so the flat part of the grid lines up with the focus zone.

- **Static:** pick the `horizon-grid-WIDTHxHEIGHT.png` closest to your screen and set it as the background (right-click → *Set as desktop background*).
- **Animated:** install the free [Lively Wallpaper](https://www.rocksdanister.com/lively/) from the Microsoft Store. Click **+ Add wallpaper**, choose `wallpaper/lively-horizon-grid/index.html`, and sparks will drift along the grid.

## 4. Settings

Tray menu → **Open settings file** opens `%APPDATA%\Horizon\config.json`. Edit it, save, then choose **Reload settings**.

| Setting | Default | Meaning |
|---|---|---|
| `FocusWidthRatio` | `0.5` | How much of the width the middle zone takes (0.3–0.8). If you change it, also change `FOCUS` in the Lively `index.html`. |
| `StashEdgePixels` | `28` | How close to the edge counts as "stash" |
| `Margin` | `16` | Gap around parked windows |
| `MinParkedHeight` | `220` | Parked windows never get shorter than this |
| `ShowZonesWhileDragging` | `true` | The neon overlay while dragging |
| `RestoreWindowsOnExit` | `true` | Put everything back when Horizon quits |
| `MediaApps` | Spotify, VLC… | Processes whose widget gets a play/pause button |
| `IgnoreProcesses` | shell bits | Processes Horizon never touches |

A log is kept in `%APPDATA%\Horizon\log.txt` if something misbehaves.

## Known limits (v0.1)

- **Apps running as administrator** can't be moved by a normal app. That's a Windows security rule. Run Horizon as admin too if you need it.
- **Some apps have a minimum width** (often 500 px+), so a parked window may poke into the focus zone. A later version can show live scaled thumbnails (the Windows DWM thumbnail API) instead of shrinking the real window, which is closer to the prototype.
- One focus window at a time. Focus *groups* (two windows side by side in focus) are a good next step.
- Not yet: the "working memory" canvas and the episodic history timeline from the talk.

## How it works (for tinkering)

| File | What it does |
|---|---|
| `src/Horizon/TrayApp.cs` | Tray icon and menu, the window-drag hook (`SetWinEventHook`), hotkeys |
| `src/Horizon/ZoneManager.cs` | Park / stash / focus logic and the column + widget layouts |
| `src/Horizon/ZoneOverlay.cs` | The click-through neon overlay drawn while dragging |
| `src/Horizon/StashWidget.cs` | The little edge widgets |
| `src/Horizon/Win.cs`, `Native.cs` | Win32 helpers for reading and moving other apps' windows |
| `.github/workflows/build.yml` | Builds `Horizon.exe` on GitHub's Windows machines |

To build locally instead: install the .NET 8 SDK, then run
`dotnet publish src/Horizon/Horizon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out`.
