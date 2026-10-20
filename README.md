# AmpTray for WireView Pro II

An unofficial, read-only desktop widget and tray icon for the Thermal Grizzly WireView Pro II 12V-2x6 power meter, with NVIDIA GPU stats alongside. Not affiliated with Thermal Grizzly or NVIDIA.

<p align="center">  
  <img src="docs/widget-expanded.png" alt="Expanded view" />&nbsp;&nbsp;&nbsp;
  <img src="docs/widget-compact.png" alt="Compact view" />
</p>

## What it shows

- Current and voltage on each of the six wires, scaled to what one wire may carry under the cable rating
- GPU temperature at the top, plus load, power and VRAM; clocks, fan, throttle reasons and more when expanded
- The four WireView temperatures and the device's active and logged faults
- A live power graph with the cable reading and the driver's GPU board power on one chart
- Board power split between the 12V-2x6 cable and the PCIe slot, and session peaks and energy

Also: CSV logging, a full-screen alarm on faults, auto-shutdown on your own thresholds, six themes, English, French, Danish and Polish, and an update banner when a new release is out.

## The device is never written to

The widget only identifies the device (raise RTS, read its welcome string) and reads the sensor block (`0x04`). There is no code path for config, calibration, flash, screen, clear-fault or bootloader commands. The port is opened per read under the shared `Global\Access_USB_Sensors` mutex, so the official app and the widget can run together. GPU data comes from the driver's own NVML; only read calls are declared.

## Install

Download from [Releases](https://github.com/borexola/AmpTray/releases):

- `AmpTray-<version>-x64.msi`: installer with Start Menu entry, clean upgrades and uninstall
- `AmpTray-<version>-win-x64-portable.zip`: unzip anywhere and run `AmpTray.exe`

Both are self-contained (no .NET install needed), Windows 10/11 x64. The installer's first page picks the widget language; unattended: `msiexec /i AmpTray-<version>-x64.msi /qn APPLANGUAGE=fr`.

## Use

- Drag to move. Double-click to switch between compact and expanded; the widget always starts compact. Compact shows only the panels you pick in Settings, expanded shows everything. Hover for the expand and settings buttons.
- Opacity goes down to 5 %. Only the panel background fades: text, bars and gauges stay solid and get an outline once the background is thin, so the readings stay legible over anything behind the widget. By default the widget turns solid while the mouse is over it.
- The GPU temperature and the FPS counter in the top bar can each be turned off (Settings → Panels → Top bar). With several monitors you can pick which screen's game the FPS counter follows; by default it follows the app in the foreground.
- Right-click the widget or tray icon for the menu. Left-click the tray icon to show or hide the widget.
- Settings pages: Panels, Theme, Alerts, Logging, Auto shutdown, Window (always on top, click-through, opacity, refresh rate, start with Windows, language, update check) and About (version you are running, manual update check, links).
- Switches: `/settings`, `/settings:Theme`, `/test-alert`.

## Starting with Windows and running before sign-in

- The widget registers itself to start when you sign in (Settings → Window → Start with Windows turns it off).
- A Windows window cannot exist before someone signs in, so protection from boot is handled by a **background service**: the same exe started as `AmpTray.exe /service` under LocalSystem. It polls the device and GPU, writes the CSV log and applies the auto-shutdown rules exactly like the widget, using the same read-only requests. While it runs, it is the one that issues the shutdown (with Windows' own countdown when a countdown is configured) and the widget shows the banner and can still cancel.
- The installer offers the service as a checkbox (on by default; `INSTALLSERVICE=0` for unattended installs). Settings → Window → Background monitoring turns it on or off later, which asks for administrator approval.
- The service reads `C:\ProgramData\AmpTray\service.json`, which the widget keeps in sync with your settings, and logs to `service.log` next to it.
- The service also provides the **FPS counter** shown at the top of the widget. It runs Intel's PresentMon (shipped as `PresentMon.exe`, MIT licence), the reference tool that counts presented frames from the Windows graphics trace, and reports frames per second for the game in the foreground (or on the chosen monitor). Without the service the slot shows n/a.

## Fault alerts

On an active device fault every monitor gets a pulsing red frame, the widget flashes, a two-tone alarm loops and Windows shows a notification. Works with the widget hidden in the tray. Press **Acknowledge** to stop it; a different new fault alerts again. A fault marked **logged** is latched on the device and is cleared in the official app.

## Auto shutdown (off by default)

Arm it in Settings or the tray menu. It runs `shutdown /s /f /t 0` when a rule holds for its duration: a WireView temperature, N or more wires above a current, a selected device fault, total power, or GPU temperature. Safeguards: at least 10 s and 2 readings in a row from the rule's own source; an amber countdown banner with Cancel and a notification while timing; short dips below the threshold don't reset the timer; timers reset on disconnect, sleep/resume and clock changes; an optional 10/30/60 s on-screen countdown; every event logged to `shutdown_events.log`; a dry-run test button.

## Build

Needs the .NET 10 SDK. `dotnet build` at the root builds `AmpTray.slnx`; `.\build\build.ps1` publishes, zips and builds the MSI into `artifacts\out` (installs WiX 5 locally if needed). Sources live in `src\AmpTray`, the installer and scripts in `build`, screenshots in `docs`. Unit tests live in `tests\AmpTray.Tests` and run with `dotnet test`; the Build workflow runs them before packaging. Pushing a tag `vX.Y.Z` makes the workflow publish both files as a GitHub release.

<details>
<summary>More screenshots</summary>
<p align="center">
  <img src="docs/settings-panels.png" alt="Settings, Panels page" width="400" />&nbsp;
  <img src="docs/settings-auto-shutdown.png" alt="Settings, Auto shutdown page" width="400" />
</p>
<p align="center">
  <img src="docs/widget-expanded-fr.png" alt="Expanded view in French" width="300" />&nbsp;
  <img src="docs/widget-wires.png" alt="Per-wire readings" width="400" />
</p>
</details>

## Logs and crash recovery

Settings → Logs shows what the widget and the background service have been doing: device connections, faults, shutdown rules, updates and errors, filterable by level, source and text. The list follows new events while you are scrolled to the end. The underlying files (`amptray.log` next to the settings, `service.log` in ProgramData) hold one JSON object per line and never grow past 2 MB; the oldest lines are dropped. If the widget crashes it writes the exception to the log and starts a fresh copy of itself; after three crashes within ten minutes it stays closed instead of looping.

## Legal

*WireView* and *Thermal Grizzly* are trademarks of Thermal Grizzly Holding GmbH; *NVIDIA* and *GeForce* are trademarks of NVIDIA Corporation, used only to identify the hardware this tool works with. Nothing from the official software or firmware is included; the two read-only requests were determined for interoperability and the code is original. Built with .NET (MIT), System.IO.Ports (MIT), Microsoft TraceEvent (MIT), WiX Toolset (MS-RL, build time only) and GitHub Actions; ships Intel PresentMon (MIT, see `PresentMon-LICENSE.txt`). Licensed under the MIT License.
