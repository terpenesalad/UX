# KAMI UX — an ultra-wide desktop for Windows

KAMI UX turns a wide monitor (or a projected PC screen) into a depth-aware desktop:

```
┌───────────────┬──────────────────────────────┬───────────────┐
│ ╲ ╲  ╲        │ │  │  │  │  │  │  │  │  │  │ │        ╱  ╱ ╱ │
│  periphery    │            FOCUS             │    periphery  │
│  windows      │   full size, where you work  │    windows    │
│  shrink as    │                              │    shrink as  │
│  they drift   │                              │    they drift │
│ ╱ ╱  ╱  out   │ │  │  │  │  │  │  │  │  │  │ │   out  ╲  ╲ ╲ │
└───────────────┴──────────[ dock ]────────────┴───────────────┘
 ▲ stash edge                                       stash edge ▲
```

- **Depth drag:** grab any window by its title bar and pull it toward a side. It drifts back and shrinks smoothly, as if being pushed further away. Let go and it stays there, live and usable. Pull it back to the middle and it grows to full size again.
- **Stash:** push a window all the way to the screen edge and it tucks into a small widget showing its icon. Music apps get a play/pause button. Click the widget to bring the window back.
- **Grid wallpaper:** KAMI UX draws a dark grid for each monitor at its exact resolution and sets it as your wallpaper. The flat part sits behind the focus zone and the curves sit behind the peripheries, so the background is your guide. Your old wallpaper comes back when KAMI UX quits.
- **Dock:** your pinned taskbar apps, in the same order, plus anything else running, in a dock that magnifies under the pointer. A dot means the app is running. Click to open or switch, shift- or middle-click for a new window. Clicking an app that's parked or stashed brings it back to focus. The Windows taskbar auto-hides while the dock is up.
- **Video screens:** press `Win + Alt + V` and draw a box around a video in any window (browser, VLC, a video call). It becomes its own borderless screen. You can resize it, scroll to zoom, or double-click to go big, and you can run several at once. `Win + Alt + A` arranges them all in a neat grid.

It's based on the ultra-wide prototypes in Scott Jenson's talk *"Are we really going to use the same Desktop UX forever?"* (Akademy 2026).

> **Status: early prototype.** KAMI UX moves real windows. If anything goes wrong, **tray icon → Bring every window back**, or quit KAMI UX. Both put everything back where it was.

---

## 1. Get the .exe

**First time:** upload this folder to a GitHub repo. GitHub's upload page **skips anything starting with a dot**, so the `.github/workflows/build.yml` file has to be created by hand: *Add file → Create new file*, type the name `.github/workflows/build.yml`, and paste its contents. Then:

1. Open the **Actions** tab. A run called **Build KAMI UX** starts by itself and takes about 3 minutes.
2. When it's green, open the run and download **KamiUX-win-x64** under *Artifacts*.
3. Unzip and run `KamiUX.exe`. You don't need an installer or .NET.

**Updating:** upload the changed files over the old ones (same names), commit, and a new build starts.

**"Windows protected your PC"?** The exe isn't code-signed, so SmartScreen warns you. Click **More info → Run anyway**.

**Start with Windows:** tray icon → **Start with Windows**.

## 2. Use it

| Do this | What happens |
|---|---|
| Drag a window by its title bar toward a side | It drifts back and shrinks, and stays where you drop it |
| Drag it back into the middle | It grows back to full size |
| Push it to the very left/right edge | It's stashed into a small widget |
| Click a stash widget | The window flies back to focus |
| Right-click or `Esc` while dragging | Cancels; the window goes back where it was |
| Click / shift-click a dock icon | Open or switch to the app / open a new window |
| `Win + Alt + ←` / `→` | Send the active window into the left / right periphery |
| `Win + Alt + ↑` | Bring the active window to full size in focus |
| `Win + Alt + ↓` | Stash the active window |
| `Win + Alt + V` | Make a video screen: drag around the video (or click for the whole window) |
| `Win + Alt + A` | Arrange all video screens in the focus zone |
| `Win + Alt + P` | Pause / resume KAMI UX |

**Video screens:**
- Drag a screen to move it, drag an edge to resize it (it keeps its shape), and scroll to zoom.
- Double-click a screen to fill the middle of the screen; double-click again to go back.
- Right-click a screen for its options.
- The source window keeps playing underneath, so leave it open (don't minimise it). You can park it in the periphery or leave it behind the screen.

**Video that fills its own window** (no KAMI UX needed):
- **Firefox:** go to `about:config`, set `full-screen-api.ignore-widgets` to `true`. The fullscreen button then fills the window instead of the monitor.
- **Chrome / Edge:** install a "windowed fullscreen" extension, for example *Windowed – floating Youtube/every website*.
- **VLC:** press `Ctrl+H` (Minimal Interface) so the video fills the window.

## 3. Settings

Tray icon → **Open settings file** opens `%APPDATA%\KAMI UX\config.json`. Edit it, save, then choose **Reload settings**.

| Setting | Default | Meaning |
|---|---|---|
| `FocusWidthRatio` | `0.5` | How much of the width is the full-size focus zone (0.3–0.8) |
| `PeripheryMinScale` | `0.45` | How small a window gets at the far edge (0.2–0.9) |
| `StashEdgePixels` | `28` | How close to the edge counts as "stash" |
| `FluidDrag` | `true` | KAMI UX handles title-bar drags (turn off to get Windows' normal dragging back) |
| `AnimateMoves` | `true` | Glide windows into place for shortcuts, dock and widgets |
| `SetWallpaper` / `RestoreWallpaperOnExit` | `true` / `true` | The generated grid wallpaper |
| `ShowDock` / `AutoHideTaskbar` | `true` / `true` | The dock, and hiding the Windows taskbar while it's up (restored on quit) |
| `DockIconSize` / `DockMagnifiedSize` | `52` / `84` | Dock icon size at rest and under the pointer |
| `KeepVideoSourcesAwake` | `true` | Stops browsers pausing the video behind a video screen. Turn off if a screen ever shows blank |
| `ShowZonesWhileDragging` | `false` | An extra zone overlay while dragging (the wallpaper already shows the zones) |
| `RestoreWindowsOnExit` | `true` | Put moved windows back when KAMI UX quits |
| `MediaApps` | Spotify, VLC… | Apps whose stash widget gets a play/pause button |
| `IgnoreProcesses` | shell bits | Apps KAMI UX never touches |

A log is kept in `%APPDATA%\KAMI UX\log.txt`.

## 4. The wallpaper on its own

KAMI UX sets the grid wallpaper itself. The `wallpaper` folder also has the same grid as static PNGs in common ultrawide sizes, plus an animated version for [Lively Wallpaper](https://www.rocksdanister.com/lively/) (`lively-horizon-grid/index.html`). Use these if you set `SetWallpaper` to `false`.

## Known limits

- **Apps running as administrator** can't be moved by a normal app; that's a Windows security rule. Run KAMI UX as admin too if you need it.
- **Some apps have a minimum size**, so in the far periphery they may not shrink as far as the preview did.
- **During a drag you see a live preview** (for some apps, like browsers, a still frame); the real window lands when you let go.
- **Maximised windows** use Windows' normal drag. KAMI UX still sizes them by depth when you drop them.
- **Video screens copy pixels from the source window,** so make the source window large for the sharpest picture.

## How it works (for tinkering)

| File | What it does |
|---|---|
| `DragController.cs` | Low-level mouse hook (own thread) that takes over title-bar drags |
| `Depth.cs` | The "pushed further away" maths: size as a function of horizontal position |
| `ThumbnailView.cs` | Live DWM previews, used for dragging, gliding and video screens |
| `ZoneManager.cs` | Where windows land, stashing, bringing back to focus |
| `Dock.cs` | The dock (pinned taskbar shortcuts + running apps) and taskbar auto-hide |
| `Cinema.cs` | Video screens and the area picker |
| `WallpaperManager.cs` | Draws and sets the per-monitor grid wallpaper |
| `TrayApp.cs` | Tray menu, shortcuts, wiring |
| `.github/workflows/build.yml` | Builds `KamiUX.exe` on GitHub's Windows machines |

Local build: install the .NET 8 SDK, then
`dotnet publish src/Horizon/Horizon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out`.
