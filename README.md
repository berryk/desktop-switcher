# Desktop Switcher

Lightweight Windows 11 virtual desktop keyboard shortcut tool. Uses native `IVirtualDesktopManager` COM API via [Slions.VirtualDesktop](https://github.com/Slions/VirtualDesktop) — no hide/show hacks.

## Hotkeys

| Shortcut | Action |
|---|---|
| Alt+1 through Alt+9 | Switch to desktop N |
| Alt+Shift+1 through Alt+9 | Move focused window to desktop N |
| Alt+Shift+P | Pin/unpin window to all desktops |

Desktops are auto-created if they don't exist yet.

## Build & Run

Requires .NET 8 SDK. On Windows:

```powershell
# Clone
git clone https://github.com/berryk/desktop-switcher.git
cd desktop-switcher

# Build
dotnet build

# Run
dotnet run

# Or publish a single .exe
dotnet publish -c Release -r win-x64 --self-contained false
# Output: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DesktopSwitcher.exe
```

## Auto-Start

Copy the published `.exe` to your startup folder:

```powershell
$startup = [Environment]::GetFolderPath('Startup')
Copy-Item bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DesktopSwitcher.exe $startup
```

## Config

Edit `config.json` next to the executable:

```json
{
  "maxDesktops": 9,
  "followWindow": false,
  "autoCreateDesktops": true
}
```

- `maxDesktops` — number of Alt+N hotkeys to register (1-9)
- `followWindow` — if true, Alt+Shift+N moves the window AND switches to that desktop
- `autoCreateDesktops` — if true, creates desktops on-the-fly when switching to one that doesn't exist

## Designed to pair with

- **PowerToys FancyZones** — window layout/tiling
- **Native Windows virtual desktops** — OS manages everything, screen sharing just works

## Known Limitation

The undocumented `IVirtualDesktopManagerInternal` COM interface GUIDs change with Windows feature updates. This app depends on [Slions.VirtualDesktop](https://www.nuget.org/packages/Slions.VirtualDesktop) to track them. If a Windows update breaks switching, update the NuGet package:

```powershell
dotnet add package Slions.VirtualDesktop --prerelease
dotnet build
```
