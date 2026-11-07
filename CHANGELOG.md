# Changelog

All notable changes to WinTiles. Each release's section becomes its GitHub release notes. Versions before 1.2.0 were released under the name Tessera.

## [1.2.0] - 2026-11-06

**Tessera is now WinTiles**, and it's brought up to date with MacTiles 1.13.

### Changed
- **New name: WinTiles.** The installer removes an installed Tessera, and the first time WinTiles runs it carries over your tiles and settings (`%APPDATA%\Tessera` → `%APPDATA%\WinTiles`), your saved service keys and the "start when I sign in" choice.
- Settings values in a hand-edited `layout.json` are clamped to the same ranges as the Settings sliders (tile size, wallpaper dim).
- Saved service keys are labelled with the service's name in Credential Manager.

### Added
- **Several News tiles, each with its own source.** *Add Live Tile → News* now asks which feed to show, with a *Popular feeds* menu (BBC, NPR, The New York Times, The Verge, Ars Technica, Hacker News). Right-click a News tile → *Change Feed…* to switch it, and give it a name if you like. You can type a site's address instead of its feed (`theverge.com`); WinTiles finds the feed. Several News tiles take turns changing headline.
- **Weather by ZIP / postal code.** Settings → Live tiles has *Or ZIP / postal code*; codes are looked up in Windows' region. Typing a ZIP into the city box works too. The Weather tile's right-click menu has *Change Location…*.
- The Weather tile and Settings credit Open-Meteo, as its data licence (CC BY 4.0) requires.

### Fixed
- **The default news feed** is a normal box with an *Apply* button, used by News tiles that don't have their own feed. A feed that can't be loaded says so on the tile, and clicking the tile opens *Change Feed…*; a failing feed isn't retried more than every 5 minutes.
- If Credential Manager refuses to save a key, the service editor says so instead of failing silently; a momentary read failure no longer leaves a service signed out until WinTiles restarts.
- Clicking a tile whose program was uninstalled or moved plays the system sound and leaves Start open.
- The layout is saved when you sign out or restart Windows.
- ComfyUI's live connection closes as soon as Start is hidden, instead of up to 90 seconds later.

### Security
- **News headlines only open web links**, so a feed can't make the News tile open a local file or launch a program through a custom link type.
- **"Allow self-signed certificate" applies only to the service's own server**, not to other sites it links to (posters) or redirects to.
- **Keys and tokens aren't forwarded on redirects to another server** (redirects are now followed by WinTiles itself).
- Values used in service addresses (Home Assistant entity IDs, Docker container IDs, Jellyfin item IDs) are escaped, and requests to Docker's named pipe are checked, so a value can't change the request.
- Web tile icons are fetched only over http(s), with a size limit; news feeds are size-limited too.
- A hand-edited wallpaper file name can't reach outside WinTiles' Wallpapers folder.
- The service editor warns when a key or password would be sent over plain HTTP to an address that isn't on your local network.
- Uninstalling with "also delete my tiles and settings" now removes WinTiles' saved service keys from Credential Manager too.

## [1.1.0] - 2026-11-05

Brings WinTiles up to date with MacTiles 1.11.

### Added
- **Wallpapers** (Settings → Start screen → Background): Color, **Picture or Video** — a picture, an animated GIF, or a video (MP4, MOV, WMV…) that loops silently, with a Dim slider; WinTiles keeps its own copy — or **Live**: Aurora, Ribbons, Bokeh or Squares, drawn by WinTiles and tinted with your color. Animated wallpapers pause when Start is hidden or covered, with animation effects off, and in battery saver. **Change Wallpaper…** in the right-click menu.
- **Web tiles**: pin any web address from the right-click menu, the ••• menu or the tray icon (**Add Web Tile…**; a copied address is filled in), or drag a link from your browser onto Start. Tiles show the site's sharpest icon (page icons, apple-touch-icon, the web app manifest, favicon.ico, then a public favicon service) and its title, kept offline. Right-click to edit, copy the link or refresh the icon.
- **Tile finish**: Flat or **Glass** — translucent tiles with a curved gloss, a polished rim, depth shading, a pointer highlight and a hover shimmer — with a Glass tint slider. **Tile shadow**: None, Subtle, Medium or Strong; it deepens on hover.
- **Running indicator**: a glowing dot on tiles and in All apps for apps with an open window (matched by AppUserModelID or program file). Right-click to **Quit** (closes its windows, so it can ask to save) or **Force Quit…** (with confirmation; not offered for Store apps that share a host process).
- **Service tiles** for 18 homelab services, talking to each one directly: Plex, Jellyfin, Emby, Sonarr, Radarr, Overseerr/Jellyseerr, Pi-hole (v6 and v5), AdGuard Home, Home Assistant, UniFi, OPNsense (with live interface traffic), Proxmox VE, TrueNAS, Unraid, Glances, Docker (Docker Desktop's named pipe or a remote API), Beszel and ComfyUI (live progress over its WebSocket). Pause DNS blocking, toggle Home Assistant entities, start/stop/restart containers, interrupt ComfyUI or clear its queue from the tile.
- **Settings → Integrations**: add, edit, test and remove connections. Secrets are kept in Windows Credential Manager. **Add Service Tile…** in the right-click menu, the ••• menu and the tray icon.
- **Hot corner as a toggle**: optionally, the bottom-left corner also puts Start away and shows the desktop when Start is open.
- layout.json stays compatible with MacTiles: web tiles, service tiles, integrations (without their secrets) and the new settings move between the two.

## [1.0.0] - 2026-10-31

First release of WinTiles for Windows 11 — a port of MacTiles 1.4 with the same features and look.

- Windows 8-style Start screen: tile groups, four tile sizes, Windows 8.1 tile packing, tile cascade and press "tilt".
- Start stays on the desktop behind your apps; bring it forward with Ctrl+Alt+Space, the Windows key (optional), the tray icon or the bottom-left hot corner.
- Just start typing to search (no search box); apps you open get keyboard focus.
- All apps view with every Start-menu app (desktop and Microsoft Store), updated automatically as apps are installed or removed.
- Drag to reorder tiles and groups; drag apps in from File Explorer to pin them.
- Live tiles: Clock, Calendar (any iCalendar feed), Weather, System, Battery, Photos, News and Desktop.
- Tile shapes (Square, Soft, Round), any background color (presets, color wheel, hex), tile size, icon-colored tiles.
- Right-click empty space for Settings and more.
- Installer for x64 and Arm64 (per-user, no admin), portable zips, sign-in startup, uninstall from Settings.
- Reads and writes the same layout.json format as MacTiles.
