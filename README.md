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

- **Depth drag:** grab any window by its title bar and pull it toward a side. It shrinks smoothly as if it's being pushed further away, and grows again as you bring it back to the middle. There are two styles, set in Settings:
  - **Live** (default): the real window gets smaller, so you can keep using it right where it is and video keeps playing.
  - **Miniature:** a scaled picture of the whole window, contents and all; click it to bring it back. Windows doesn't let an app draw another app's window smaller *and* stay clickable, so you choose which matters more.
- **Make room:** windows don't pile on top of each other. When one lands, its neighbours slide aside, and any that get pushed toward the sides shrink.
- **Stash:** push a window all the way to the screen edge and it tucks into a small widget showing its icon. Music apps get a play/pause button. Click the widget to bring the window back.
- **Living grid:** a dark grid sits behind your desktop icons. The flat part is behind the focus zone and the curves are behind the sides. As you move a window, the grid lines around it light up and trail off behind it. Window edges that land on a line glow brightest, snap onto it, and pulse when you let go. Your old wallpaper comes back when KAMI UX quits.
- **Settings app:** the gear at the end of the dock opens KAMI UX Settings. Every option is there and changes apply instantly.
- **KAMI Files:** a minimal, dark, Finder-style file browser. It has a sidebar, breadcrumbs, big icons and photo thumbnails (or a list), instant filter and deep search (Enter in the search box), and drag and drop, rename, copy/cut/paste, new folder and Recycle Bin. The dock's Files icon opens it.
- **Clean desktop:** desktop icons are hidden while KAMI UX runs and come back when it quits.
- **Your own OS look:** while KAMI UX runs, Windows switches to dark mode (Explorer, Settings, Start, and apps that follow the system), and every window gets the same minimal near-black title bar with no coloured border. Everything goes back to how it was when you quit.
- **Dock:** your pinned taskbar apps, in the same order, plus anything else running, in a dock that magnifies under the pointer. A dot means the app is running. Click to open or switch, shift- or middle-click for a new window. Clicking an app that's parked or stashed brings it back to focus. The Windows taskbar is hidden completely while the dock is up, so it can't pop up when your mouse touches the bottom edge.
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
| Drag a window by its title bar toward a side | The whole window shrinks into the distance and stays there, live |
| Click a far-off window (or its dock icon, or Alt+Tab to it) | It glides back to full size in the middle |
| Drag a far-off window around | It grows toward the middle and shrinks toward the edge |
| Right-click a far-off window | Bring to focus / close it |
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
| `SetWallpaper` / `RestoreWallpaperOnExit` | `true` / `true` | The grid wallpaper |
| `LiveGrid` | `true` | Grid lines glow around windows you move (off = still wallpaper) |
| `SnapToGrid` / `SnapDistance` | `true` / `14` | Window edges click onto grid lines within this many pixels |
| `DarkMode` | `true` | Windows dark mode while KAMI UX runs |
| `MinimalWindowChrome` | `true` | The same minimal dark title bar on every window |
| `ShowDock` / `AutoHideTaskbar` | `true` / `true` | The dock, and hiding the Windows taskbar completely while it's up (restored on quit) |
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
- **In Miniature mode, far-off windows are for looking, not clicking into.** A click brings the window back. (Live mode doesn't have this limit.)
- **Miniature mode only: browsers may pause while far off.** Chrome, Edge and Firefox stop drawing windows they think are hidden, so a far-off browser can show a still frame. To keep them live, turn off their occlusion tracking:
  - **Chrome / Edge:** add `--disable-features=CalculateNativeWinOcclusion` to the shortcut.
  - **Firefox:** in `about:config`, set `widget.windows.window_occlusion_tracking.enabled` to `false`.
  Or use a video screen (`Win+Alt+V`), which keeps playing without this.
- **Explorer's layout stays Explorer's.** KAMI UX makes it dark and minimal, but it can't redesign Explorer's insides.
- **Maximised windows** use Windows' normal drag. KAMI UX still sizes them by depth when you drop them.
- **Video screens copy pixels from the source window,** so make the source window large for the sharpest picture.

## How it works (for tinkering)

| File | What it does |
|---|---|
| `DragController.cs` | Low-level mouse hook (own thread) that takes over title-bar drags |
| `Depth.cs` | The "pushed further away" maths: size as a function of horizontal position |
| `ThumbnailView.cs` | Live DWM previews, used for dragging, gliding and video screens |
| `ZoneManager.cs` | Where windows land, stashing, bringing back to focus |
| `ParkedView.cs` | The live miniature that stands in for a far-off window |
| `GridGeometry.cs` / `LiveGrid.cs` | The grid's lines (shared by wallpaper, glow and snapping) and the glowing grid behind the icons |
| `WindowTheme.cs` | Dark mode and the minimal title bars |
| `Dock.cs` | The dock (pinned taskbar shortcuts + running apps) and taskbar auto-hide |
| `Cinema.cs` | Video screens and the area picker |
| `WallpaperManager.cs` | Draws and sets the per-monitor grid wallpaper |
| `TrayApp.cs` | Tray menu, shortcuts, wiring |
| `.github/workflows/build.yml` | Builds `KamiUX.exe` on GitHub's Windows machines |

Local build: install the .NET 8 SDK, then
`dotnet publish src/Horizon/Horizon.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out`.
