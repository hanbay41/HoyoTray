# HoyoTray

[![Latest release](https://img.shields.io/github/v/release/hanbay41/HoyoTray)](../../releases/latest)
[![Downloads](https://img.shields.io/github/downloads/hanbay41/HoyoTray/total)](../../releases)
[![License: 0BSD](https://img.shields.io/github/license/hanbay41/HoyoTray)](LICENSE)
![Windows](https://img.shields.io/badge/platform-Windows-0078D6)

A tiny Windows system-tray app that shows your **Genshin Impact Original Resin**,
**Honkai: Star Rail Trailblaze Power** and **Zenless Zone Zero Battery Charge**,
pulled from HoYoLAB's Real-Time Notes (daily notes).

A lightweight resin tracker and stamina reminder for your desktop: see at a glance how much
resin, trailblaze power or battery you have and exactly when it caps, without opening
the HoYoLAB app or a game launcher.

Hover the icon for a quick glance, or left/right-click for the full card:

![Menu](docs/menu.png)

## Features

- Resin, Trailblaze Power and battery with progress bars, "full in" countdown and the clock time it caps
- Genshin commissions, HSR daily training and ZZZ engagement at a glance
- Settings window to choose which games are shown, show several accounts per game,
  and pick the tray icon (Original Resin, Trailblaze Power or Battery Charge)
- Optional daily rewards: auto-claim the HoYoLAB daily check-in, and get notified about (or auto-redeem)
  redemption codes posted on HoYoLAB after livestreams
- Polls HoYoLAB every 20 minutes (and after waking from sleep); counts regen locally in between,
  so numbers stay accurate to the minute without hammering the API
- One-time login through an embedded HoYoLAB page, no copying cookies by hand
- Follows the Windows light/dark theme
- "Start with Windows" toggle
- ~2 MB single exe

## Install

### With winget

```
winget install hoyotray
```

> [!NOTE]
> The winget package is awaiting approval ([pull request](https://github.com/microsoft/winget-pkgs/pull/450805)).
> Until it's merged, use the manual download below.

winget installs the .NET 8 Desktop Runtime for you if it's missing. Start the app by typing `hoyotray`
in the Run dialog (Win+R) or a terminal, then turn on **Start with Windows** in its menu so you don't have to again.

To update later, exit HoyoTray first (its exe can't be replaced while running), then run `winget upgrade hoyotray`.

### Manual download

1. Download `HoyoTray.exe` from the [latest release](../../releases/latest) and put it wherever you like.
2. Run it. If Windows asks for the **.NET 8 Desktop Runtime**, install it
   ([download](https://dotnet.microsoft.com/download/dotnet/8.0), "Desktop Runtime").

### First run

1. Log in to HoYoLAB in the window that opens. It closes by itself once your login works.
2. In HoYoLAB → Battle Chronicle → settings, make sure **Real-Time Notes** is enabled for each game.

## Settings

Click the tray icon → **Settings…**

![Settings](docs/settings.png)

- Toggle whole games on or off. Disabled games aren't requested from HoYoLAB at all.
- Every account linked to your HoYoLAB login is listed under its game. By default only the
  highest-level account per game is shown; switch on any others you want to see.
- Pick the tray icon.

Settings are saved in `%LOCALAPPDATA%\HoyoTray\settings.json`.

## Daily rewards

Per game, in Settings → **Daily rewards**:

- **Daily check-in** (off by default): claims the HoYoLAB daily check-in once a day, after the
  daily reset (midnight UTC+8). Cards show "✓ Checked in" once it's done.
- **Redemption codes**: HoyoTray watches the redemption-code section of each game's HoYoLAB page,
  where livestream codes appear.
  - *Notify* (default): a notification and a "Redeem new codes" item in the menu; click to redeem.
  - *Auto*: redeems new codes for every shown account of that game and tells you what it redeemed.
  - *Off*: ignores codes.

If HoYoverse answers with a security check (captcha), HoyoTray doesn't try to get around it: the menu
offers to open the official check-in or redemption page so you can finish it yourself.

Only codes posted on HoYoLAB are found, mainly livestream codes; codes shared only on social media won't be.
Rewards are only claimed while HoyoTray is running.

Windows SmartScreen may warn about an unrecognised app because the exe isn't code-signed.
The exe is built from this repo's source by GitHub Actions (see `.github/workflows/build.yml`),
so you can check exactly what went into it, or build it yourself.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish -c Release -o publish
```

The exe ends up in `publish\HoyoTray.exe`.

## Privacy

- Your HoYoLAB login cookies are stored only on your PC, encrypted with Windows DPAPI
  (readable only by your Windows user), in `%LOCALAPPDATA%\HoyoTray`.
- They're sent only to HoYoLAB's own servers. Nothing else is contacted.
- To log out completely, exit the app and delete `%LOCALAPPDATA%\HoyoTray`.
- The last fetched numbers are cached in `cache.json` (same folder) so they show instantly on launch.
  It holds only stamina values, nicknames and UIDs.
- A small troubleshooting log (`log.txt`, same folder) records refresh results and error codes.
  It never contains your cookies.

## Limitations

- Uses HoYoLAB's **unofficial, undocumented** API (the same one community tools like genshin.py
  use). HoYoverse can change it at any time, which may break the app until it's updated.
- Read-only by default: it doesn't change anything on your account and never touches the game client.
  The optional daily check-in and code redemption are automated actions on your account (the same requests
  the official web pages send); they're off / notify-only unless you enable them.
- Global (HoYoLAB) servers only, not China (miyoushe) servers.

## Disclaimer

Not affiliated with or endorsed by HoYoverse. Genshin Impact, Honkai: Star Rail, Zenless Zone Zero
and related names and artwork are trademarks and copyrights of HoYoverse. The icons in `Assets/`
are HoYoverse artwork and are **not** covered by this project's license.

## License

The source code is released under the [0BSD license](LICENSE): do whatever you want with it,
no attribution required, no warranty.
