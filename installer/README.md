# HP Control

A lightweight replacement for the HP app's **System control**, **Battery manager** and **Audio** pages, made for the **HP OmniBook X Flip 16**.

## Install
1. **Extract the zip** (right-click → *Extract All*). It won't run from inside the zip.
   - Tip: before extracting, right-click the zip → *Properties* → tick **Unblock** → OK. This stops Windows warning about the files.
2. Make sure the **HP app (myHP)** is installed. Install it from the Microsoft Store if needed. HP Control borrows its audio components (DTS and Realtek) during install: see *HP's audio components* below. Fans and battery work without it.
3. Double-click **install.cmd** and approve the admin prompt.
   - If Windows shows "Windows protected your PC", click **More info → Run anyway**. The app isn't code-signed, so SmartScreen doesn't recognise it.
4. HP Control opens when the install finishes, and a tray icon appears. It installs to `C:\Program Files\HP Control`.

After installing, you can uninstall the HP app if you like. Re-run `install.cmd` at any time to repair.

## Updates
- The tray helper checks for a new version once a day (one small request to GitHub). When there is one, it shows a notification and an **Install update** item in the tray menu. You can also check under **Settings → Updates**, or turn the daily check off there.
- Installing is always your click. The update is downloaded from the latest release on [github.com/Hellohi3654/HPControl](https://github.com/Hellohi3654/HPControl/releases), checked against the SHA-256 checksum in the release notes, and installed with the same `install.cmd` (Windows asks for admin). HP Control closes while it installs and opens again afterwards.
- You can also download a release zip yourself and run `install.cmd` from it.

### Your settings across updates
- Before a new version touches your settings, it saves a copy to `C:\ProgramData\HPControl\backups\`, named after the version it came from (for example `settings from HP Control 1.7.5 (v13) 2026-11-25 1830.json`). The 15 newest are kept.
- An update only changes a setting or a profile that is still exactly at its old default. Anything you changed yourself is left alone. Each change is listed in the log and on the Home page.
- To undo, use **Settings → Restore a backup…** and pick the file.

## HP's audio components
The DTS presets/EQ and Realtek/Fortemedia microphone controls use HP's own audio components. The installer copies them from your installed HP app (`DTSApo4xWinRTComponent.dll`, `FMAPOCTL.dll`, `RtkAudioCore.dll`), plus Microsoft's C++ runtime they need, into `C:\Program Files\HP Control\fx`. It also keeps a copy in `C:\ProgramData\HPControl\fx-saved` (and in an `fx` folder next to the installer), so reinstalls and updates still have them after the HP app is uninstalled. These files are HP's: they're never included in HP Control's downloads or uploaded anywhere.

The installer checks for an HP laptop and .NET Framework 4.8 (built into Windows 10 and 11). On other HP models it asks before continuing.

## Uninstall
Run **uninstall.cmd** (there's also a copy in `C:\Program Files\HP Control`). It does three things:
- stops and removes the service;
- restores your power plan values and sets the HP mode back to Smart Sense;
- removes the app.

## Auto (recommended) and Manual
The Home page has two choices:
- **Auto (recommended, the default):** HP Control picks the HP mode, keeps the fan off or quiet, boosts power for heavy loads and games while plugged in, and saves battery. It uses one profile for plugged in and one for battery (below).
- **Manual HP mode:** you pick one of HP's six modes yourself, like the HP app. HP Control's quiet fan curve still applies unless you turn it off (System control → Fan).

## Profiles
- **Adaptive** (default when plugged in): HP Quiet for everyday use. When something heavy keeps running (load above 45 % for about 8 seconds), it switches to HP Performance for full power, and back about a minute after the load ends. "Load" means CPU usage **or** chip power (CPU + graphics), so games that mostly use the graphics count too. Short bursts (opening an app, a page loading) don't count.
  - Measured on this laptop (plugged in, on a desk), the sustained CPU + graphics power limit each HP mode sets is: Performance 35 W, Smart Sense 30 W (drops to 20 W once warm), Balanced 30 W, Quiet 12 W, Cool 13 W, Power saver 10 W. Short bursts can reach 37 W.
- **Battery smart** (default on battery): Power saver for everyday use. Balanced while something heavy keeps running (about 15 seconds of load), back to Power saver about 45 seconds after it ends. It never uses Performance: on battery HP's limits are lower anyway (about 26 W Performance, 20 W Balanced), and the extra power would mostly cost battery life. It goes by load, not CPU temperature, because the CPU sits a little warmer while HP Control keeps the fan off.
- **Quiet:** HP Quiet mode with a fan speed limit (3,000 rpm by default). If the fan still goes over the limit, CPU power is trimmed until it's back under.
- **Max performance:** HP Performance all the time.
- **HP Smart Sense:** lets HP's firmware pick the mode (HP Control's fan curve still applies unless you turn it off).

Every profile has a **Max fan speed**, which also caps HP Control's fan curve.

### Games (automatic)
The **Games** rule is on by default. When the app in front looks like a game and is actually using the CPU or graphics, HP Control switches to **Max performance** while plugged in (not on battery). It goes back once you leave the game, or about 2 minutes after it goes idle (menus, paused).

A game is recognised by where it's installed (Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot, Battle.net and similar libraries), by game-engine files next to it (Unity, Unreal, Godot, store SDKs) and by a short list of well-known names. Launchers, browsers, video players and everyday apps never count. If a game is missed, add its process name to the rule (System control → Rules → Games).

## How the fan control works
The embedded controller (EC) normally drives the fan. It runs it whenever the board sensor is above about 33 °C (nearly always, even at 40 °C CPU doing nothing) and jumps to loud speeds in Performance.

**HP Control drives the fan** (System control → Fan, on by default) using HP's firmware fan test feature:
- **Off** while the CPU is cool and the laptop is idle. It comes on when the CPU averages 60 °C over 30 s and is still warm, when a heavy job has kept the chip power up for 20 s, when the CPU hits 85 °C, or when the inside of the laptop gets warm. Short bursts never wake it.
- **A smooth curve** once on: fan speed by CPU temperature (1,950 rpm, the lowest speed the fan has, all the way from 55 °C to 66 °C, then rising to 5,800 rpm at 95 °C), with a gentle head start from chip power when a heavy job begins, and a minimum speed from the internal sensor during long heavy sessions.
- **Ramped:** it speeds up by at most 120 rpm a second (faster above 75 °C so it keeps up with a heavy load) and slows down by 60 rpm a second, so it never jumps.
- **Off again** as soon as the load has gone and the CPU averages 55 °C over 15 s (after at least 30 s on). It then winds down quickly and stops from about 2,300 rpm. The slow internal sensor only keeps it on above 62 °C.

The HP mode then only sets CPU power, not fan noise, so switching to Performance no longer makes the fan suddenly loud.

"Internal" is an EC sensor inside the laptop, not the outside of the case. It reads well above what the case feels like (about 40 °C at idle).

**Safety:**
- The fan goes back to the EC if the CPU stays at 95 °C for 15 s (or hits 99 °C), or the internal sensor averages 72 °C over a minute. The EC then runs the fan as usual. HP Control takes over again once things have cooled.
- It also goes back when HP Control stops, restarts or is uninstalled, before sleep, if a sensor stops answering, if the fan doesn't follow, if Fan always on is enabled, and if the control loop stalls (a watchdog).
- The CPU also protects itself by slowing down at its temperature limit.

All of these (the curves, thresholds and ramp rates) are adjustable in Advanced mode.

## Simple and Advanced
**Simple mode** shows the profiles, max fan speed, rules, the temperature graph and the heat test.

Turn on **Show advanced settings** (in System control or Settings) to add:
- the per-profile curve editor: mode ladder, fan limit curve, CPU budget curve, load boost and "when busy, switch to";
- the fan curves (by CPU temperature, chip power and internal sensor), thresholds and ramp rates;
- timing settings and CPU power limiting;
- sensor, limiter and polling settings.

**Rules** can switch profile, cap fan speed or cap CPU power. They can trigger on quiet hours, calls and meetings, specific apps, a game in front (auto-detected), fullscreen, tablet mode, idle or low battery, and can be limited to plugged in only.

**Also included:**
- **Fan always on** (an HP BIOS setting, under System control → Fan).
- **Ctrl+Alt+F**, which cycles through profiles.

**Tip:** if the HP app is still installed, keep its System control page closed while HP Control is in Auto. Otherwise the two apps keep changing the fan mode, and the HP app may show stale audio settings.

## Battery use
- The service reads a few cheap sensors every 2 s on AC and every 4–8 s on battery. It only speeds up (every 2 s) while the fan is changing speed. While the fan is off it checks the fan every 15 s. It uses no kernel driver.
- On battery it never switches to Performance, and Battery smart stays in Power saver unless it gets warm under load.
- The tray helper is event-driven and sits at about 0 % CPU. Game detection only runs when the app in front changes, and the answer is remembered per app.
- The main window only refreshes while it's open (every 10 s when minimised).

## Files and logs
- `C:\ProgramData\HPControl\` holds:
  - `settings.json`, and `backups\` with copies from before each update;
  - `fx-saved\`, the saved copy of HP's audio components;
  - `service.log`, capped at about 1 MB;
  - `power-journal.json`, present only while a CPU limit is active.
- `%LOCALAPPDATA%\HPControl\` holds the app's `ui.log`, audio status files, and downloaded updates (`updates\`).
- Install log: `%TEMP%\hpcontrol-install.log`.
